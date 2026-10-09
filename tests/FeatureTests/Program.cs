using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

var falhas = 0;
void Check(bool ok, string o) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {o}"); if (!ok) falhas++; }

var raiz = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(raiz);

// Minimal PE: MZ header + PE header with an import of the given DLLs, enough for PeUtils.
static void FakeExe(string path, bool is64, params string[] imports)
{
    // Build a tiny PE32+ with one section holding an import directory.
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0x5A4D); w.Write(new byte[0x3A]); w.Write(0x80u);           // e_lfanew = 0x80
    w.Write(new byte[0x80 - ms.Length]);
    w.Write(0x00004550u);                                                        // PE\0\0
    w.Write((ushort)(is64 ? 0x8664 : 0x14C)); w.Write((ushort)1);                // machine, 1 section
    w.Write(new byte[12]);
    ushort optSize = (ushort)(is64 ? 240 : 224);
    w.Write(optSize); w.Write((ushort)0x22);
    long opt = ms.Position;
    w.Write((ushort)(is64 ? 0x20B : 0x10B));
    w.Write(new byte[optSize - 2]);
    long secTable = ms.Position;
    // section: VA 0x1000, raw 0x400
    w.Write(System.Text.Encoding.ASCII.GetBytes(".idata\0\0"));
    w.Write(0x1000u); w.Write(0x1000u); w.Write(0x1000u); w.Write(0x400u);
    w.Write(new byte[16]);
    w.Write(new byte[0x400 - ms.Length]);
    // import descriptors at RVA 0x1000, names after
    int n = imports.Length;
    uint namesRva = (uint)(0x1000 + (n + 1) * 20);
    var names = new List<byte>();
    for (int i = 0; i < n; i++)
    {
        w.Write(new byte[12]); w.Write(namesRva + (uint)names.Count); w.Write(0u);
        names.AddRange(System.Text.Encoding.ASCII.GetBytes(imports[i] + "\0"));
    }
    w.Write(new byte[20]);
    w.Write(names.ToArray());
    w.Write(new byte[0x1400 - ms.Length]);
    // data directory[1] = import table
    ms.Position = opt + (is64 ? 112 : 96) + 8;
    w.Write(0x1000u); w.Write((uint)((n + 1) * 20));
    File.WriteAllBytes(path, ms.ToArray());
}

try
{
    // ---------------- 1. deteccao ----------------
    var unity = Directory.CreateDirectory(Path.Combine(raiz, "Hollow Thing")).FullName;
    FakeExe(Path.Combine(unity, "HollowThing.exe"), true, "kernel32.dll");
    File.WriteAllText(Path.Combine(unity, "UnityPlayer.dll"), "x");
    Check(GameDetection.Avaliar(unity).EhJogo, "Unity (UnityPlayer.dll) e jogo");

    var py = Directory.CreateDirectory(Path.Combine(raiz, "Python312")).FullName;
    FakeExe(Path.Combine(py, "python.exe"), true, "kernel32.dll");
    Check(!GameDetection.Avaliar(py).EhJogo, "pasta Python nao e jogo (nome)");

    var ferramenta = Directory.CreateDirectory(Path.Combine(raiz, "WinBoxLike")).FullName;
    FakeExe(Path.Combine(ferramenta, "winbox64.exe"), true, "kernel32.dll", "user32.dll", "gdi32.dll");
    var v = GameDetection.Avaliar(ferramenta);
    Check(!v.EhJogo, $"app comum (so user32/gdi32) nao e jogo — {v.Motivo}");

    var electron = Directory.CreateDirectory(Path.Combine(raiz, "SomeChatApp")).FullName;
    FakeExe(Path.Combine(electron, "SomeChatApp.exe"), true, "d3d11.dll", "dxgi.dll", "kernel32.dll");
    File.WriteAllText(Path.Combine(electron, "libcef.dll"), "x");
    v = GameDetection.Avaliar(electron);
    Check(!v.EhJogo, $"app CEF que importa d3d11 nao e jogo — {v.Motivo}");

    var semMarca = Directory.CreateDirectory(Path.Combine(raiz, "OldGame2008")).FullName;
    FakeExe(Path.Combine(semMarca, "oldgame.exe"), false, "d3d9.dll", "dinput8.dll", "kernel32.dll");
    v = GameDetection.Avaliar(semMarca);
    Check(v.EhJogo, $"jogo antigo sem SDK: d3d9 + dinput8 e jogo — {v.Motivo}");

    var cliente = Directory.CreateDirectory(Path.Combine(raiz, "MySteamCopy")).FullName;
    FakeExe(Path.Combine(cliente, "steam.exe"), false, "d3d9.dll", "xinput1_3.dll");
    File.WriteAllText(Path.Combine(cliente, "steam_api.dll"), "x");
    Check(!GameDetection.Avaliar(cliente).EhJogo, "pasta do cliente Steam nao e jogo");

    Check(!GameDetection.EntradaDeLojaEhJogo(new GameInfo { Name = "Proton 9.0", InstallDir = unity, Store = GameStore.Steam, AppId = "2805730" }, out _),
          "Proton (Steam) fora da grade");
    Check(!GameDetection.EntradaDeLojaEhJogo(new GameInfo { Name = "Some Game Soundtrack", InstallDir = unity, Store = GameStore.Steam, AppId = "1" }, out _),
          "Soundtrack fora da grade");
    var vazio = Directory.CreateDirectory(Path.Combine(raiz, "NoExe")).FullName;
    Check(!GameDetection.EntradaDeLojaEhJogo(new GameInfo { Name = "Real Game", InstallDir = vazio, Store = GameStore.Epic }, out _),
          "entrada de loja sem exe fora da grade");
    Check(GameDetection.EntradaDeLojaEhJogo(new GameInfo { Name = "Hollow Thing", InstallDir = unity, Store = GameStore.Steam, AppId = "12345" }, out _),
          "jogo normal da Steam fica");

    // ---------------- 2. snapshot + log + restaurar ----------------
    var jogo = Directory.CreateDirectory(Path.Combine(raiz, "SnapGame")).FullName;
    FakeExe(Path.Combine(jogo, "SnapGame.exe"), true, "d3d11.dll", "xinput1_4.dll");
    File.WriteAllText(Path.Combine(jogo, "dxgi.dll"), "ORIGINAL-DXGI-DO-JOGO");
    File.WriteAllText(Path.Combine(jogo, "d3d9.dll"), "ORIGINAL-D3D9");
    Directory.CreateDirectory(Path.Combine(jogo, "data"));
    File.WriteAllText(Path.Combine(jogo, "data", "level1.pak"), "conteudo");
    File.WriteAllText(Path.Combine(jogo, "nvngx_dlss.dll"), "DLSS-DO-JOGO-v1");
    static Dictionary<string, string> Fotografar(string d) =>
        Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(d, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))),
                          StringComparer.OrdinalIgnoreCase);
    var antes = Fotografar(jogo);

    var info = SnapshotService.Garantir(jogo, "SnapGame");
    Check(info is not null && info.Limpo && info.Arquivos == antes.Count, $"fotografia: {info?.Arquivos} arquivos, limpo={info?.Limpo}");

    var linhas = new List<InstallLog.Linha>();
    using (var sessao = new InstallLog(jogo, [jogo], new SyncProgress<InstallLog.Linha>(linhas.Add)))
    {
        sessao.Etapa("teste");
        // passa pelo BackupService
        File.WriteAllText(Path.Combine(raiz, "reshade-lib.dll"), "RESHADE");
        BackupService.Copiar(jogo, Path.Combine(raiz, "reshade-lib.dll"), Path.Combine(jogo, "dxgi.dll"), "reshade");
        // NAO passam pelo BackupService (os buracos que a fotografia cobre)
        File.Move(Path.Combine(jogo, "d3d9.dll"), Path.Combine(jogo, "d3d9.dll.pre-dxvk"));
        File.WriteAllText(Path.Combine(jogo, "d3d9.dll"), "DXVK");
        File.WriteAllText(Path.Combine(jogo, "nvngx_dlss.dll"), "DLSS-NOSSO-v3");
        Directory.CreateDirectory(Path.Combine(jogo, "host64"));
        File.WriteAllText(Path.Combine(jogo, "host64", "dlss5-feed-host64.exe"), "HOST");
        Directory.CreateDirectory(Path.Combine(jogo, "reshade-shaders", "Shaders"));
        File.WriteAllText(Path.Combine(jogo, "reshade-shaders", "Shaders", "DLSS5_Feed.fx"), "fx");
        File.WriteAllText(Path.Combine(jogo, "ReShade.ini"), "[ADDON]");
        Thread.Sleep(300);
        sessao.Fechar(jogo, ok: true);
        SnapshotService.RegistrarTocados(jogo, sessao.Tocados);
        Check(sessao.Tocados.ContainsKey("host64\\dlss5-feed-host64.exe"), "observador viu o host64 criado fora do BackupService");
        Check(sessao.Tocados.ContainsKey("d3d9.dll.pre-dxvk"), "observador viu o rename para .pre-dxvk");
        Check(linhas.Any(l => l.Tipo == InstallLog.Tipo.Resumo && l.Texto.Contains("dxgi.dll")), "log descreve o dxgi.dll no resumo");
    }
    foreach (var l in linhas) Console.WriteLine("      " + l);

    // o jogo roda depois e escreve coisas suas + o ReShade escreve log
    File.WriteAllText(Path.Combine(jogo, "savegame.sav"), "SAVE");
    File.WriteAllText(Path.Combine(jogo, "ReShade.log"), "log");
    File.WriteAllText(Path.Combine(jogo, "SnapGame_d3d9.log"), "dxvk log");

    var plano = SnapshotService.Planejar(jogo)!;
    Console.WriteLine($"      devolver: {string.Join(", ", plano.Devolver)}");
    Console.WriteLine($"      apagar:   {string.Join(", ", plano.Apagar)}");
    Console.WriteLine($"      mantidos: {string.Join(", ", plano.Mantidos)}");
    Check(plano.Devolver.Contains("dxgi.dll") && plano.Devolver.Contains("d3d9.dll") && plano.Devolver.Contains("nvngx_dlss.dll"),
          "plano devolve dxgi, d3d9 e nvngx_dlss");
    Check(!plano.Apagar.Contains("savegame.sav"), "plano nao toca no save do jogo");
    Check(plano.Apagar.Contains("ReShade.log") && plano.Apagar.Contains("SnapGame_d3d9.log"), "plano apaga logs que a cadeia escreve em jogo");

    var r = SnapshotService.Restaurar(jogo)!;
    if (BackupService.TemBackup(jogo)) BackupService.Restaurar(jogo);
    var depois = Fotografar(jogo);
    depois.Remove("savegame.sav");
    var iguais = antes.Count == depois.Count && antes.All(kv => depois.TryGetValue(kv.Key, out var h) && h == kv.Value);
    Check(r.Falhas.Count == 0, $"restaurar sem falhas ({r.Devolvidos} devolvidos, {r.Apagados} apagados)");
    Check(iguais, "pasta bit a bit como antes (fora o save do jogo)");
    if (!iguais)
        foreach (var k in antes.Keys.Union(depois.Keys))
            if (!antes.TryGetValue(k, out var a) || !depois.TryGetValue(k, out var b) || a != b) Console.WriteLine($"      difere: {k}");
    Check(File.Exists(Path.Combine(jogo, "savegame.sav")), "save do jogo continua la");
    Check(!Directory.Exists(Path.Combine(jogo, "host64")) && !Directory.Exists(Path.Combine(jogo, "reshade-shaders")),
          "pastas que nos criamos sairam");
    SnapshotService.Descartar(jogo);
    Check(!SnapshotService.Tem(jogo), "fotografia descartada depois do restaurar limpo");

    // ---------------- 3. teclas ----------------
    var cfg = new LauncherConfig();
    cfg.ReShadeKeys["KeyOverlay"] = new ReShadeHotkeys.Atalho(0x71, true, false, false).ParaIni();  // Ctrl+F2
    cfg.ReShadeKeys["KeyEffects"] = new ReShadeHotkeys.Atalho(0x91, false, false, false).ParaIni(); // Scroll Lock
    var ini = Path.Combine(raiz, "ReShade.ini");
    File.WriteAllText(ini, "[GENERAL]\nEffectSearchPaths=.\\\n\n[INPUT]\nKeyOverlay=36,0,0,0\nKeyScreenshot=44,0,0,0\n");
    Check(ReShadeHotkeys.Aplicar(ini, cfg), "teclas aplicadas");
    var texto = File.ReadAllText(ini);
    Console.WriteLine(texto.Replace("\n", "\n      "));
    Check(texto.Contains("KeyOverlay=113,1,0,0") && texto.Contains("KeyEffects=145,0,0,0")
          && texto.Contains("KeyScreenshot=44,0,0,0") && texto.Contains("EffectSearchPaths"), "ini com as teclas novas e o resto intacto");
    Check(!ReShadeHotkeys.Aplicar(ini, cfg), "reaplicar sem mudanca nao reescreve");
    Check(ReShadeHotkeys.Descrever(ReShadeHotkeys.Atual(cfg).Overlay) == "Ctrl+F2", $"descricao: {ReShadeHotkeys.Descrever(ReShadeHotkeys.Atual(cfg).Overlay)}");

    // ---------------- 4. banco: veredicto em cache ----------------
    var v1 = GameDetection.AvaliarComCache(semMarca);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var v2 = GameDetection.AvaliarComCache(semMarca);
    Check(v1 == v2, $"veredicto em cache igual ({sw.Elapsed.TotalMilliseconds:0.0} ms na segunda)");
    Check(GameDatabase.MesmaLista(
        [new GameInfo { Name = "A", InstallDir = "C:\\X", Store = GameStore.Steam }],
        [new GameInfo { Name = "A", InstallDir = "c:\\x\\", Store = GameStore.Steam }]), "MesmaLista ignora caixa e barra final");
}
finally
{
    // nada do teste fica no banco do usuario
    try
    {
        using var c = new SqliteConnection($"Data Source={GameDatabase.DbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM log_instalacao WHERE pasta LIKE $p; DELETE FROM veredicto_pasta WHERE pasta LIKE $p;";
        cmd.Parameters.AddWithValue("$p", raiz + "%");
        cmd.ExecuteNonQuery();
    }
    catch (Exception ex) { Console.WriteLine("limpeza do banco: " + ex.Message); }
    try { Directory.Delete(raiz, true); } catch { }
}

Console.WriteLine(falhas == 0 ? "\nTUDO OK" : $"\n{falhas} FALHAS");
return falhas == 0 ? 0 : 1;

sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }
