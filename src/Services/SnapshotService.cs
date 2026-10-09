using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RenoDXLauncher.Services;

/// <summary>
/// A fotografia de uma pasta de jogo tirada ANTES de o launcher escrever nela pela primeira vez.
///
/// O BackupService guarda o que cada servico lembra de declarar. Na pratica nem todo caminho
/// declara: o DXVK renomeia para `.pre-dxvk`, o Feeder cria `host64\` e `dlss5-feed.cfg`, o
/// ReShade escreve `ReShade.log` e `reshade-shaders\` ao rodar. O manifesto nao conhece nenhum
/// desses, e o "restaurar" deixava todos para tras.
///
/// A fotografia responde a pergunta pelo outro lado: em vez de "o que declaramos ter mexido", ela
/// sabe o que a pasta ERA. Tres pecas:
///
///   1. o inventario: todo arquivo da pasta, com tamanho e data — barato, e o que diz se um
///      arquivo e novo;
///   2. as copias: os arquivos que a cadeia pode sobrescrever (proxies de DLL, runtimes de DLSS,
///      Streamline, inis de ReShade/DXVK/dgVoodoo), copiados bit a bit para a pasta do launcher,
///      com hash;
///   3. os tocados: tudo o que o observador de arquivos viu as instalacoes fazerem (InstallLog).
///
/// Restaurar devolve as copias dos arquivos que as instalacoes tocaram, apaga o que as instalacoes
/// criaram e que nao estava no inventario, e so entao chama o BackupService para o resto.
///
/// O que NAO e tocado: arquivo que mudou sem que uma instalacao nossa o tocasse. Isso e o jogo se
/// atualizando, ou a pessoa mexendo a mao, e devolver uma DLL de duas versoes atras por cima de
/// uma atualizacao da loja seria quebrar o jogo em nome de restaura-lo. O relatorio diz quais
/// ficaram, e por que.
/// </summary>
public static class SnapshotService
{
    public static string RaizDasCopias { get; } = Path.Combine(AppPaths.DataDir, "snapshots");

    /// <summary>Teto do inventario. Uma pasta de jogo com mais arquivos que isto existe (bibliotecas
    /// de mods, jogos de mundo aberto com assets soltos), e nela o inventario para — as copias, que
    /// sao o que importa para devolver, continuam completas.</summary>
    private const int MaxArquivos = 400_000;

    /// <summary>
    /// Nomes que alguma peca da cadeia escreve, sobrescreve ou renomeia. So estes sao COPIADOS: a
    /// lista e curta de proposito, porque copiar a pasta inteira de um jogo de 150 GB nao e backup,
    /// e mudanca de disco.
    /// </summary>
    private static readonly Regex Sensiveis = new(
        @"^(dxgi|d3d8|d3d9|d3d10|d3d10_1|d3d10core|d3d11|d3d12|dinput8|opengl32|winmm|version|winhttp|dbghelp|"
        + @"nvngx.*|sl\..*|nvapi64|amd_fidelityfx.*|libxess.*|ffx_.*|dlss.*|"
        + @"reshade.*|dgvoodoo.*|dxvk.*|optiscaler.*|nvngx\.ini|renodx.*|dlss5.*|"
        + @".*\.addon(32|64)?)\.(dll|ini|conf|json|cfg|addon32|addon64|exe)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Arquivos e pastas NOVOS que so a cadeia cria — ainda que o observador nao os tenha visto
    /// nascer (o ReShade escreve log e cache quando o jogo roda, nao quando instalamos). Um arquivo
    /// novo fora desta lista e fora dos "tocados" e do jogo ou da pessoa, e fica.
    /// </summary>
    private static readonly Regex NovosDaCadeia = new(
        @"(^|\\)(reshade[^\\]*\.(log|ini|dll|json)|reshadepreset[^\\]*\.ini|dlss5-feed[^\\]*|"
        + @"renodx[^\\]*|[^\\]*\.addon(32|64)?|[^\\]*\.dxvk-cache|[^\\]*_(d3d9|d3d10|d3d10_1|d3d11|dxgi)\.log|"
        + @"dxvk\.conf|dgvoodoo[^\\]*|nvngx_dlssnr\.dll|nvngx_dlssd\.dll|[^\\]*\.pre-dxvk|"
        + @"[^\\]*\.renodx-(bak|ours)|[^\\]*\.dlss5-multi-api|\.dlss5-[^\\]*)$"
        + @"|(^|\\)(reshade-shaders|host64|vklayer)(\\|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record Info(long Id, string Pasta, DateTime Criado, bool Limpo, int Arquivos, string CopiasDir);

    public static Info? Ler(string? pasta)
    {
        if (string.IsNullOrEmpty(pasta)) return null;
        return GameDatabase.Com<Info?>("ler snapshot", c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, pasta, criado, limpo, arquivos, copias_dir FROM snapshot WHERE pasta = $p";
            cmd.Parameters.AddWithValue("$p", Normalizar(pasta));
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new Info(r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2)).ToLocalTime(),
                           r.GetInt32(3) != 0, r.GetInt32(4), r.GetString(5))
                : null;
        }, null);
    }

    public static bool Tem(string? pasta) => Ler(pasta) is not null;

    /// <summary>
    /// Tira a fotografia, se esta pasta ainda nao tem uma. Chamar antes de qualquer escrita.
    ///
    /// "Limpo" diz se a pasta estava intocada no momento: uma pasta onde uma versao anterior do
    /// launcher ja instalou nao tem mais o estado original para fotografar — o manifesto do
    /// BackupService, quando existe, e o que guarda aquele original, e o restaurar o usa depois.
    /// </summary>
    public static Info? Garantir(string pasta, string? jogo, IProgress<string>? progress = null)
    {
        var existente = Ler(pasta);
        if (existente is not null) return existente;
        if (!Directory.Exists(pasta)) return null;

        var raiz = Normalizar(pasta);
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(Localization.L.T("Snapshot_Taking"));

        var limpo = !BackupService.TemBackup(pasta) && !ParecePastaJaInstalada(pasta);
        var copiasDir = Path.Combine(RaizDasCopias, $"{DateTime.Now:yyyyMMdd-HHmmss}-{NomeSeguro(raiz)}");

        var inventario = new List<(string Rel, long Bytes, long Escrito, string? Sha, bool Copiado)>();
        var opcoes = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 12,
        };
        long bytesCopiados = 0;
        try
        {
            foreach (var fi in new DirectoryInfo(raiz).EnumerateFiles("*", opcoes))
            {
                if (inventario.Count >= MaxArquivos) break;
                var rel = Path.GetRelativePath(raiz, fi.FullName);
                if (rel.StartsWith(BackupService.PastaDeBackup, StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = null;
                var copiado = false;
                if (Sensiveis.IsMatch(fi.Name))
                {
                    try
                    {
                        var destino = Path.Combine(copiasDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
                        File.Copy(fi.FullName, destino, overwrite: true);
                        sha = Hash(destino);
                        copiado = true;
                        bytesCopiados += fi.Length;
                    }
                    catch (Exception ex) { Log.Warn($"snapshot copiar {rel}: {ex.Message}"); }
                }
                inventario.Add((rel, fi.Length, fi.LastWriteTimeUtc.Ticks, sha, copiado));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"snapshot {raiz}: {ex.Message}");
            return null;
        }

        var id = GameDatabase.Com<long>("gravar snapshot", c =>
        {
            using var tx = c.BeginTransaction();
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO snapshot (pasta, jogo, criado, limpo, arquivos, copias_dir)
                VALUES ($p, $j, $c, $l, $n, $d);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$p", raiz);
            ins.Parameters.AddWithValue("$j", (object?)jogo ?? DBNull.Value);
            ins.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            ins.Parameters.AddWithValue("$l", limpo ? 1 : 0);
            ins.Parameters.AddWithValue("$n", inventario.Count);
            ins.Parameters.AddWithValue("$d", copiasDir);
            var novoId = (long)ins.ExecuteScalar()!;

            using var arq = c.CreateCommand();
            arq.Transaction = tx;
            arq.CommandText = """
                INSERT OR REPLACE INTO snapshot_arquivo (snapshot_id, rel, bytes, escrito, sha256, copiado)
                VALUES ($s, $r, $b, $e, $h, $c)
                """;
            var ps = arq.Parameters.Add(new SqliteParameter { ParameterName = "$s", Value = novoId });
            var pr = arq.Parameters.Add(new SqliteParameter { ParameterName = "$r" });
            var pb = arq.Parameters.Add(new SqliteParameter { ParameterName = "$b" });
            var pe = arq.Parameters.Add(new SqliteParameter { ParameterName = "$e" });
            var ph = arq.Parameters.Add(new SqliteParameter { ParameterName = "$h" });
            var pc = arq.Parameters.Add(new SqliteParameter { ParameterName = "$c" });
            foreach (var a in inventario)
            {
                pr.Value = a.Rel;
                pb.Value = a.Bytes;
                pe.Value = a.Escrito;
                ph.Value = (object?)a.Sha ?? DBNull.Value;
                pc.Value = a.Copiado ? 1 : 0;
                arq.ExecuteNonQuery();
            }
            tx.Commit();
            return novoId;
        }, -1);
        if (id < 0) return null;

        Log.Info($"snapshot: {raiz} — {inventario.Count} arquivos, {inventario.Count(a => a.Copiado)} copiados "
                 + $"({bytesCopiados / 1024 / 1024} MB), limpo={limpo}, {relogio.ElapsedMilliseconds} ms");
        progress?.Report(Localization.L.T("Snapshot_Taken", inventario.Count, inventario.Count(a => a.Copiado)));
        return Ler(pasta);
    }

    /// <summary>Anota o que uma instalacao tocou. A primeira acao registrada vale para sempre: um
    /// arquivo que NOS criamos continua "criado" mesmo que a proxima instalacao o reescreva.</summary>
    public static void RegistrarTocados(string pasta, IReadOnlyDictionary<string, InstallLog.Tipo> tocados)
    {
        var s = Ler(pasta);
        if (s is null || tocados.Count == 0) return;
        GameDatabase.Com("registrar tocados", c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO snapshot_tocado (snapshot_id, rel, acao, quando)
                VALUES ($s, $r, $a, $q)
                """;
            cmd.Parameters.AddWithValue("$s", s.Id);
            var pr = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$r" });
            var pa = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$a" });
            cmd.Parameters.AddWithValue("$q", DateTime.UtcNow.ToString("o"));
            foreach (var (rel, tipo) in tocados)
            {
                if (Path.IsPathRooted(rel)) continue;   // fora da pasta: nao e desta fotografia
                pr.Value = rel;
                pa.Value = tipo.ToString();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        });
    }

    // ---------- restaurar ----------

    /// <param name="Devolver">Arquivos que voltam ao conteudo da fotografia.</param>
    /// <param name="Apagar">Arquivos novos que a cadeia criou.</param>
    /// <param name="PastasNovas">Pastas que nao existiam e so tem coisa nossa.</param>
    /// <param name="Mantidos">Arquivos que mudaram sem passar por nos — ficam, com o motivo.</param>
    public record Plano(List<string> Devolver, List<string> Apagar, List<string> PastasNovas, List<string> Mantidos)
    {
        public int Total => Devolver.Count + Apagar.Count;
    }

    private record Original(long Bytes, long Escrito, string? Sha, bool Copiado);

    private static (Dictionary<string, Original> Inv, Dictionary<string, string> Tocados) Carregar(long id)
    {
        var inv = new Dictionary<string, Original>(StringComparer.OrdinalIgnoreCase);
        var toc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        GameDatabase.Com("carregar snapshot", c =>
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT rel, bytes, escrito, sha256, copiado FROM snapshot_arquivo WHERE snapshot_id = $s";
                cmd.Parameters.AddWithValue("$s", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    inv[r.GetString(0)] = new Original(r.GetInt64(1), r.GetInt64(2),
                                                       r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4) != 0);
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT rel, acao FROM snapshot_tocado WHERE snapshot_id = $s";
                cmd.Parameters.AddWithValue("$s", id);
                using var r = cmd.ExecuteReader();
                while (r.Read()) toc[r.GetString(0)] = r.GetString(1);
            }
        });
        return (inv, toc);
    }

    /// <summary>O que o restaurar faria, sem fazer. E o que a confirmacao mostra.</summary>
    public static Plano? Planejar(string pasta)
    {
        var s = Ler(pasta);
        if (s is null) return null;
        var raiz = Normalizar(pasta);
        var (inv, tocados) = Carregar(s.Id);
        var plano = new Plano([], [], [], []);

        // 1. o que estava la e mudou
        foreach (var (rel, orig) in inv)
        {
            var abs = Path.Combine(raiz, rel);
            var tocadoPorNos = tocados.ContainsKey(rel) || BackupConhece(raiz, rel);
            if (!File.Exists(abs))
            {
                if (tocadoPorNos && orig.Copiado) plano.Devolver.Add(rel);
                continue;
            }
            if (!orig.Copiado) continue;
            var fi = new FileInfo(abs);
            if (fi.Length == orig.Bytes && fi.LastWriteTimeUtc.Ticks == orig.Escrito) continue;
            if (orig.Sha is not null && Hash(abs).Equals(orig.Sha, StringComparison.OrdinalIgnoreCase)) continue;
            if (tocadoPorNos) plano.Devolver.Add(rel);
            else plano.Mantidos.Add(rel);
        }

        // 2. o que nao estava la
        var opcoes = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 12,
        };
        var inventarioCompleto = s.Arquivos < MaxArquivos;
        // Toda pasta que tinha algum arquivo na fotografia, para nao perguntar arquivo por arquivo.
        var pastasDoInventario = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in inv.Keys)
            for (var d = Path.GetDirectoryName(rel); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (!pastasDoInventario.Add(d)) break;
        try
        {
            foreach (var f in Directory.EnumerateFiles(raiz, "*", opcoes))
            {
                var rel = Path.GetRelativePath(raiz, f);
                if (rel.StartsWith(BackupService.PastaDeBackup, StringComparison.OrdinalIgnoreCase)) continue;
                if (inv.ContainsKey(rel)) continue;
                var nosso = tocados.TryGetValue(rel, out var acao) && acao is "Criado" or "Renomeado" or "Alterado";
                // Sem inventario completo, "nao esta no inventario" nao prova que e novo: so o que
                // vimos nascer, ou o que tem nome da cadeia, sai.
                if (nosso || NovosDaCadeia.IsMatch(rel) && (inventarioCompleto || nosso))
                    plano.Apagar.Add(rel);
            }
            foreach (var d in Directory.EnumerateDirectories(raiz, "*", opcoes))
            {
                var rel = Path.GetRelativePath(raiz, d);
                if (rel.StartsWith(BackupService.PastaDeBackup, StringComparison.OrdinalIgnoreCase)) continue;
                if (pastasDoInventario.Contains(rel)) continue;
                if (tocados.ContainsKey(rel) || NovosDaCadeia.IsMatch(rel)) plano.PastasNovas.Add(rel);
            }
        }
        catch (Exception ex) { Log.Warn($"snapshot planejar {raiz}: {ex.Message}"); }

        plano.Devolver.Sort(StringComparer.OrdinalIgnoreCase);
        plano.Apagar.Sort(StringComparer.OrdinalIgnoreCase);
        return plano;
    }

    public record Resultado(int Devolvidos, int Apagados, List<string> Falhas, List<string> Mantidos);

    /// <summary>
    /// Devolve a pasta a fotografia. Chamar ANTES do BackupService.Restaurar: este passo apaga o
    /// que o manifesto nao conhece, e o manifesto depois devolve o que ele guardou de instalacoes
    /// anteriores a fotografia — nessa ordem, o original mais antigo e o que fica.
    /// </summary>
    public static Resultado? Restaurar(string pasta, IProgress<string>? progress = null)
    {
        var s = Ler(pasta);
        var plano = Planejar(pasta);
        if (s is null || plano is null) return null;
        var raiz = Normalizar(pasta);
        var (inv, _) = Carregar(s.Id);
        var falhas = new List<string>();
        int devolvidos = 0, apagados = 0;

        foreach (var rel in plano.Devolver)
        {
            var copia = Path.Combine(s.CopiasDir, rel);
            var alvo = Path.Combine(raiz, rel);
            try
            {
                if (!File.Exists(copia)) { falhas.Add(rel); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(alvo)!);
                File.Copy(copia, alvo, overwrite: true);
                if (inv.TryGetValue(rel, out var o))
                {
                    if (o.Sha is not null && !Hash(alvo).Equals(o.Sha, StringComparison.OrdinalIgnoreCase))
                    {
                        falhas.Add(rel);
                        continue;
                    }
                    // A data tambem volta: e ela que a proxima comparacao olha primeiro.
                    File.SetLastWriteTimeUtc(alvo, new DateTime(o.Escrito, DateTimeKind.Utc));
                }
                devolvidos++;
                BackupService.Anotar(raiz, "snapshot", "devolveu o original", rel);
                progress?.Report(rel);
            }
            catch (Exception ex) { falhas.Add(rel); Log.Warn($"snapshot devolver {rel}: {ex.Message}"); }
        }

        foreach (var rel in plano.Apagar)
        {
            var alvo = Path.Combine(raiz, rel);
            try
            {
                if (!File.Exists(alvo)) continue;
                File.SetAttributes(alvo, FileAttributes.Normal);
                File.Delete(alvo);
                InstalledModRegistry.Remove(alvo);
                apagados++;
                BackupService.Anotar(raiz, "snapshot", "apagou o nosso", rel);
            }
            catch (Exception ex) { falhas.Add(rel); Log.Warn($"snapshot apagar {rel}: {ex.Message}"); }
        }

        foreach (var rel in plano.PastasNovas.OrderByDescending(p => p.Length))
        {
            var alvo = Path.Combine(raiz, rel);
            try
            {
                if (Directory.Exists(alvo) && !Directory.EnumerateFileSystemEntries(alvo).Any())
                    Directory.Delete(alvo);
            }
            catch (Exception ex) { Log.Warn($"snapshot rmdir {rel}: {ex.Message}"); }
        }

        Log.Info($"snapshot: {raiz} restaurado ({devolvidos} devolvidos, {apagados} apagados, "
                 + $"{falhas.Count} falhas, {plano.Mantidos.Count} mantidos)");
        return new Resultado(devolvidos, apagados, falhas, plano.Mantidos);
    }

    /// <summary>Esquece a fotografia e apaga as copias. Depois de um restaurar limpo ela ja nao
    /// descreve nada que precise voltar, e a proxima instalacao tira uma nova.</summary>
    public static void Descartar(string pasta)
    {
        var s = Ler(pasta);
        if (s is null) return;
        GameDatabase.Com("descartar snapshot", c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var t in new[] { "snapshot_tocado", "snapshot_arquivo" })
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {t} WHERE snapshot_id = $s";
                cmd.Parameters.AddWithValue("$s", s.Id);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM snapshot WHERE id = $s";
                cmd.Parameters.AddWithValue("$s", s.Id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        });
        try
        {
            if (Directory.Exists(s.CopiasDir)
                && Path.GetFullPath(s.CopiasDir).StartsWith(RaizDasCopias, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(s.CopiasDir, recursive: true);
        }
        catch (Exception ex) { Log.Warn($"snapshot apagar copias {s.CopiasDir}: {ex.Message}"); }
    }

    // ---------- auxiliares ----------

    /// <summary>Ha sinais de uma instalacao anterior a este recurso? Entao a fotografia nao e do
    /// estado original, e o restaurar depende do manifesto antigo para o resto.</summary>
    private static bool ParecePastaJaInstalada(string pasta)
    {
        try
        {
            return Directory.EnumerateFiles(pasta, "*.renodx-ours", SearchOption.TopDirectoryOnly).Any()
                   || Directory.EnumerateFiles(pasta, "*.addon64", SearchOption.TopDirectoryOnly)
                       .Any(f => Path.GetFileName(f).StartsWith("renodx", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool BackupConhece(string pasta, string rel)
    {
        try
        {
            if (!BackupService.TemBackup(pasta)) return false;
            var m = BackupService.Ler(pasta);
            return m.Substituidos.Any(x => x.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase))
                   || m.Acrescentados.Any(x => x.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string Normalizar(string pasta) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(pasta));

    private static string NomeSeguro(string pasta)
    {
        var nome = Path.GetFileName(pasta);
        var limpo = new string(nome.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
        return limpo.Length > 40 ? limpo[..40] : limpo;
    }

    private static string Hash(string caminho)
    {
        try
        {
            using var fs = File.OpenRead(caminho);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch { return ""; }
    }
}
