using System.IO;
using Microsoft.Data.Sqlite;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// O banco local do launcher (SQLite, em %LOCALAPPDATA%\RenoDXLauncher\launcher.db).
///
/// Guarda o que custa caro descobrir e quase nunca muda:
///
///   jogos              a ultima lista de jogos encontrada. A grade abre com ela na hora, e a
///                      varredura das lojas e do disco roda depois, por cima.
///   veredicto_pasta    "esta pasta e um jogo?" para a varredura de disco. A resposta exige ler
///                      cabecalho de executavel e listar a pasta; com a data da pasta na chave,
///                      a segunda abertura nao paga nada disso.
///   snapshot*          o estado de cada pasta de jogo ANTES da primeira instalacao, e tudo que
///                      as instalacoes tocaram depois (ver SnapshotService).
///   log_instalacao     o registro de cada instalacao, linha por linha, para a tela mostrar.
///
/// Tudo aqui e cache ou historico: um banco apagado ou corrompido faz o launcher voltar a
/// varrer, e nunca o impede de abrir. Por isso nenhuma funcao daqui lanca.
/// </summary>
public static class GameDatabase
{
    public static string DbPath { get; } = Path.Combine(AppPaths.DataDir, "launcher.db");

    private static readonly object Trava = new();
    private static bool _pronto;

    /// <summary>Versao do esquema. Subir este numero recria as tabelas de CACHE (jogos e
    /// veredictos); as de historico (snapshot, log) so ganham colunas, nunca sao apagadas.</summary>
    private const int VersaoDoEsquema = 1;

    private static SqliteConnection Abrir()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString());
        c.Open();
        lock (Trava)
        {
            if (!_pronto)
            {
                Criar(c);
                _pronto = true;
            }
        }
        return c;
    }

    private static void Criar(SqliteConnection c)
    {
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "PRAGMA synchronous=NORMAL;");
        var versao = Convert.ToInt32(Scalar(c, "PRAGMA user_version;") ?? 0);
        if (versao != VersaoDoEsquema)
        {
            Exec(c, "DROP TABLE IF EXISTS jogos;");
            Exec(c, "DROP TABLE IF EXISTS veredicto_pasta;");
        }
        Exec(c, """
            CREATE TABLE IF NOT EXISTS jogos (
                pasta        TEXT PRIMARY KEY COLLATE NOCASE,
                nome         TEXT NOT NULL,
                loja         INTEGER NOT NULL,
                app_id       TEXT,
                steam_app_id INTEGER,
                exe_hint     TEXT,
                capa         TEXT,
                sobra        INTEGER NOT NULL DEFAULT 0,
                sobra_bytes  INTEGER NOT NULL DEFAULT 0,
                visto_em     TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS veredicto_pasta (
                pasta        TEXT PRIMARY KEY COLLATE NOCASE,
                escrita      INTEGER NOT NULL,
                eh_jogo      INTEGER NOT NULL,
                motivo       TEXT
            );
            CREATE TABLE IF NOT EXISTS snapshot (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                pasta        TEXT NOT NULL UNIQUE COLLATE NOCASE,
                jogo         TEXT,
                criado       TEXT NOT NULL,
                limpo        INTEGER NOT NULL,
                arquivos     INTEGER NOT NULL,
                copias_dir   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snapshot_arquivo (
                snapshot_id  INTEGER NOT NULL,
                rel          TEXT NOT NULL COLLATE NOCASE,
                bytes        INTEGER NOT NULL,
                escrito      INTEGER NOT NULL,
                sha256       TEXT,
                copiado      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (snapshot_id, rel)
            );
            CREATE TABLE IF NOT EXISTS snapshot_tocado (
                snapshot_id  INTEGER NOT NULL,
                rel          TEXT NOT NULL COLLATE NOCASE,
                acao         TEXT NOT NULL,
                quando       TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, rel)
            );
            CREATE TABLE IF NOT EXISTS log_instalacao (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                pasta        TEXT NOT NULL COLLATE NOCASE,
                sessao       TEXT NOT NULL,
                quando       TEXT NOT NULL,
                tipo         TEXT NOT NULL,
                texto        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_log_pasta ON log_instalacao (pasta, id);
            """);
        Exec(c, $"PRAGMA user_version={VersaoDoEsquema};");
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>Roda um bloco com conexao aberta; falha vira log e o valor padrao.</summary>
    internal static T Com<T>(string oQue, Func<SqliteConnection, T> f, T padrao)
    {
        try
        {
            using var c = Abrir();
            return f(c);
        }
        catch (Exception ex)
        {
            Log.Warn($"db {oQue}: {ex.Message}");
            return padrao;
        }
    }

    internal static void Com(string oQue, Action<SqliteConnection> f) =>
        Com<bool>(oQue, c => { f(c); return true; }, false);

    // ---------- jogos ----------

    /// <summary>A ultima lista gravada, so com as pastas que ainda existem.</summary>
    public static List<GameInfo> LerJogos() => Com("ler jogos", c =>
    {
        var lista = new List<GameInfo>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT pasta, nome, loja, app_id, steam_app_id, exe_hint, capa, sobra, sobra_bytes FROM jogos";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var pasta = r.GetString(0);
            if (!Directory.Exists(pasta)) continue;
            lista.Add(new GameInfo
            {
                InstallDir = pasta,
                Name = r.GetString(1),
                Store = (GameStore)r.GetInt32(2),
                AppId = r.IsDBNull(3) ? null : r.GetString(3),
                SteamAppId = r.IsDBNull(4) ? null : r.GetInt32(4),
                ExeHint = r.IsDBNull(5) ? null : r.GetString(5),
                LocalCoverPath = r.IsDBNull(6) ? null : r.GetString(6),
                EhSobra = r.GetInt32(7) != 0,
                SobraBytes = r.GetInt64(8),
            });
        }
        return lista
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }, new List<GameInfo>());

    /// <summary>Troca a lista inteira pela que a varredura acabou de achar.</summary>
    public static void GravarJogos(IReadOnlyList<GameInfo> jogos) => Com("gravar jogos", c =>
    {
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM jogos;", tx);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO jogos (pasta, nome, loja, app_id, steam_app_id, exe_hint, capa, sobra, sobra_bytes, visto_em)
            VALUES ($pasta, $nome, $loja, $app, $steam, $hint, $capa, $sobra, $bytes, $visto)
            """;
        var p = new[] { "$pasta", "$nome", "$loja", "$app", "$steam", "$hint", "$capa", "$sobra", "$bytes", "$visto" }
            .Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
        var agora = DateTime.UtcNow.ToString("o");
        foreach (var g in jogos)
        {
            p[0].Value = g.InstallDir;
            p[1].Value = g.Name;
            p[2].Value = (int)g.Store;
            p[3].Value = (object?)g.AppId ?? DBNull.Value;
            p[4].Value = (object?)g.SteamAppId ?? DBNull.Value;
            p[5].Value = (object?)g.ExeHint ?? DBNull.Value;
            p[6].Value = (object?)g.LocalCoverPath ?? DBNull.Value;
            p[7].Value = g.EhSobra ? 1 : 0;
            p[8].Value = g.SobraBytes;
            p[9].Value = agora;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    });

    /// <summary>A lista mudou desde a ultima gravacao? Compara pasta, nome e loja.</summary>
    public static bool MesmaLista(IReadOnlyList<GameInfo> a, IReadOnlyList<GameInfo> b)
    {
        if (a.Count != b.Count) return false;
        static string Chave(GameInfo g) =>
            $"{Path.TrimEndingDirectorySeparator(g.InstallDir).ToLowerInvariant()}|{g.Name}|{g.Store}|{g.EhSobra}";
        var conjunto = a.Select(Chave).ToHashSet(StringComparer.Ordinal);
        return b.All(g => conjunto.Contains(Chave(g)));
    }

    // ---------- veredicto da varredura de disco ----------

    public static (bool EhJogo, string? Motivo)? LerVeredicto(string pasta, long escrita) =>
        Com<(bool, string?)?>("ler veredicto", c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT eh_jogo, motivo FROM veredicto_pasta WHERE pasta = $p AND escrita = $e";
            cmd.Parameters.AddWithValue("$p", pasta);
            cmd.Parameters.AddWithValue("$e", escrita);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return (r.GetInt32(0) != 0, r.IsDBNull(1) ? null : r.GetString(1));
        }, null);

    public static void GravarVeredicto(string pasta, long escrita, bool ehJogo, string? motivo) =>
        Com("gravar veredicto", c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO veredicto_pasta (pasta, escrita, eh_jogo, motivo)
                VALUES ($p, $e, $j, $m)
                """;
            cmd.Parameters.AddWithValue("$p", pasta);
            cmd.Parameters.AddWithValue("$e", escrita);
            cmd.Parameters.AddWithValue("$j", ehJogo ? 1 : 0);
            cmd.Parameters.AddWithValue("$m", (object?)motivo ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });

    /// <summary>Esquece todos os veredictos: o proximo "Atualizar" reavalia cada pasta.</summary>
    public static void LimparVeredictos() => Com("limpar veredictos", c => Exec(c, "DELETE FROM veredicto_pasta;"));

    // ---------- log de instalacao ----------

    public record LinhaDeLog(DateTime Quando, string Sessao, string Tipo, string Texto);

    public static void GravarLog(string pasta, string sessao, IEnumerable<InstallLog.Linha> linhas) =>
        Com("gravar log", c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO log_instalacao (pasta, sessao, quando, tipo, texto)
                VALUES ($p, $s, $q, $t, $x)
                """;
            var pp = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$p", Value = pasta });
            var ps = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$s", Value = sessao });
            var pq = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$q" });
            var pt = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$t" });
            var px = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$x" });
            foreach (var l in linhas)
            {
                pq.Value = l.Quando.ToString("o");
                pt.Value = l.Tipo.ToString();
                px.Value = l.Texto;
                cmd.ExecuteNonQuery();
            }
            // Historico sem fim nao serve a ninguem: as ultimas 20 sessoes por pasta bastam.
            using var limpa = c.CreateCommand();
            limpa.Transaction = tx;
            limpa.CommandText = """
                DELETE FROM log_instalacao WHERE pasta = $p AND sessao NOT IN (
                    SELECT sessao FROM log_instalacao WHERE pasta = $p
                    GROUP BY sessao ORDER BY MAX(id) DESC LIMIT 20)
                """;
            limpa.Parameters.AddWithValue("$p", pasta);
            limpa.ExecuteNonQuery();
            tx.Commit();
        });

    /// <summary>As linhas da ultima instalacao feita nesta pasta.</summary>
    public static List<LinhaDeLog> LerUltimoLog(string pasta) => Com("ler log", c =>
    {
        var lista = new List<LinhaDeLog>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT quando, sessao, tipo, texto FROM log_instalacao
            WHERE pasta = $p AND sessao = (SELECT sessao FROM log_instalacao WHERE pasta = $p ORDER BY id DESC LIMIT 1)
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("$p", pasta);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            lista.Add(new LinhaDeLog(DateTime.Parse(r.GetString(0)).ToLocalTime(), r.GetString(1), r.GetString(2), r.GetString(3)));
        return lista;
    }, new List<LinhaDeLog>());
}
