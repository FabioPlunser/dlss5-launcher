using System.IO;
using System.Text.RegularExpressions;

namespace RenoDXLauncher.Services;

/// <summary>
/// "Esta pasta e um jogo?" — por evidencia, e nao por "tem um .exe que nao parece instalador".
///
/// A pergunta antiga aceitava qualquer aplicativo: toda pasta de programa tem um executavel com
/// nome de programa, e a varredura de disco devolvia Python, ferramentas de rede, editores, o
/// proprio cliente da Steam. As listas de nomes do ExeLocator continuam valendo para escolher
/// QUAL exe e o jogo; o que decide SE ha jogo agora e o que so jogo tem:
///
///   forte   motor de jogo ou SDK de loja na pasta: UnityPlayer.dll, *-Win64-Shipping.exe,
///           steam_api64.dll, EOSSDK, Galaxy64.dll, nvngx_dlss.dll, um .pck do Godot...
///           Um so desses basta.
///   medio   um executavel que importa uma API grafica E uma de controle/entrada (XInput,
///           DirectInput) — aplicativo de desktop desenha na tela, mas nao le gamepad; ou
///           arquivos de dados de jogo (.pak, .vpk, .bsa, .assets, .bundle, .rpf...).
///   contra  sinais de aplicativo: .git, node_modules, python.exe, app.asar do Electron,
///           Qt sem motor nenhum, o cliente de uma loja.
///
/// Pontos: forte = aceita; contra = recusa (a nao ser que haja um forte); medio soma.
/// </summary>
public static class GameDetection
{
    public readonly record struct Veredicto(bool EhJogo, string Motivo);

    /// <summary>Arquivos (por nome exato) que so aparecem em pasta de jogo.</summary>
    private static readonly HashSet<string> MarcadoresFortes = new(StringComparer.OrdinalIgnoreCase)
    {
        // motores
        "UnityPlayer.dll", "GameAssembly.dll", "UnityCrashHandler64.exe", "UnityCrashHandler32.exe",
        "CrySystem.dll", "CryRenderD3D11.dll", "engine2.dll",
        // SDKs de loja embutidos no jogo — o aplicativo comum nao fala com Steam/Epic/GOG
        "steam_api.dll", "steam_api64.dll", "steam_appid.txt", "steamclient64.dll",
        "EOSSDK-Win64-Shipping.dll", "EOSSDK-Win32-Shipping.dll",
        "Galaxy.dll", "Galaxy64.dll", "uplay_r1_loader64.dll", "uplay_r164.dll", "upc_r2_loader64.dll",
        "XGameRuntime.dll", "MicrosoftGame.config",
        // RPG Maker (XP a MZ) e Ren'Py
        "rpg_core.js", "rmmz_core.js", "Game.rgss3a", "Game.rgss2a", "Game.rgssad", "renpy.exe",
        // upscalers e middleware de jogo
        "nvngx_dlss.dll", "nvngx_dlssg.dll", "sl.interposer.dll", "libxess.dll",
        "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll", "ffx_fsr2_api_x64.dll",
        "bink2w64.dll", "bink2w32.dll", "binkw32.dll", "binkw64.dll",
        "fmod.dll", "fmod64.dll", "fmodstudio.dll", "fmodstudio64.dll", "fmodex.dll", "fmodex64.dll",
        "PhysX3_x64.dll", "PhysX_64.dll", "PhysXCore.dll", "PhysXCore64.dll", "APEX_Clothing_x64.dll",
        "wwise.dll", "AkSoundEngine.dll", "GFSDK_SSAO_D3D11.win64.dll", "GFSDK_Aftermath_Lib.x64.dll",
        "RenoDX.ini",
    };

    /// <summary>Extensoes de pacote de dados de jogo.</summary>
    private static readonly HashSet<string> ExtensoesDeDados = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pak", ".vpk", ".bsa", ".ba2", ".assets", ".bundle", ".rpf", ".forge", ".big", ".wad",
        ".pck", ".arc", ".bnk", ".wem", ".ucas", ".utoc", ".gpk", ".upk", ".xnb", ".rgss3a", ".rpa",
        ".rgssad", ".dat0", ".bdt", ".bhd", ".tiger", ".sarc",
    };

    /// <summary>Executaveis que so o CLIENTE de uma loja tem. A pasta deles e o cliente, nao um jogo.</summary>
    private static readonly HashSet<string> Clientes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "Battle.net.exe", "EADesktop.exe",
        "Origin.exe", "upc.exe", "UbisoftConnect.exe", "RockstarService.exe",
        "itch.exe", "Playnite.DesktopApp.exe", "heroic.exe", "AmazonGames.exe", "XboxPcApp.exe",
    };

    /// <summary>Sinais de aplicativo. So contam quando NENHUM marcador forte apareceu: ha jogo
    /// em Qt e jogo com Python embutido, e esses tem o motor por perto.</summary>
    private static readonly HashSet<string> MarcadoresDeApp = new(StringComparer.OrdinalIgnoreCase)
    {
        "python.exe", "pythonw.exe", "node.exe", "java.exe", "javaw.exe", "Qt5Core.dll", "Qt6Core.dll",
        "libcef.dll", "chrome_elf.dll", "msedgewebview2.exe", "electron.exe", "app.asar", "package.json",
        "Code.exe", "devenv.exe", "obs64.exe", "vlc.exe", "mpv.exe", "7z.exe", "WinRAR.exe", "git.exe",
        "blender.exe",
    };

    private static readonly HashSet<string> PastasDeApp = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "site-packages", ".venv", "venv", "__pycache__", ".vscode", ".idea",
    };

    /// <summary>Nomes de pasta de programa conhecidos que nunca sao jogo, por mais que tenham
    /// um executavel grafico: ferramentas de desenvolvimento, drivers, utilitarios.</summary>
    private static readonly Regex NomesDeApp = new(
        @"^(python\d*|nodejs|git|jetbrains|microsoft.*|windows.*|adobe.*|autodesk.*|android.*|"
        + @"nvidia.*|amd|intel|obs-studio|vscode|microsoft vs code|docker.*|vmware.*|virtualbox|oracle|"
        + @"7-zip|winrar|notepad\+\+|blender.*|unity( hub)?|epic games launcher|unreal engine|ue_\d.*|"
        + @"steam|steamcmd|gog galaxy|ea desktop|origin|ubisoft( game launcher)?|battle\.net|"
        + @"rockstar games launcher|msi.*|razer.*|logitech.*|corsair.*|asus.*|steelseries.*|"
        + @"discord|spotify|zoom|teams|chrome|firefox|mozilla.*|opera.*|brave.*|"
        + @"cuda|nvidia gpu computing toolkit|wsl|cygwin\d*|msys\d*|mingw\d*|strawberry|perl|ruby\d*|go|"
        + @"dotnet|reshade|dlss swapper|special k|rtss|rivatuner.*|msi afterburner|hwinfo.*|cpu-z|gpu-z)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ApisGraficas = ["d3d9", "d3d10", "d3d11", "d3d12", "dxgi", "vulkan-1", "opengl32", "d3d8", "ddraw"];
    private static readonly string[] ApisDeEntrada = ["xinput", "dinput", "gameinput", "hid.dll", "sdl2", "sdl3", "steam_api"];

    private static readonly EnumerationOptions Busca = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        MaxRecursionDepth = 4,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Avalia a pasta, com o veredicto guardado no banco por data de escrita da pasta. A data da
    /// pasta muda quando um filho direto entra ou sai — o que cobre jogo instalado, desinstalado e
    /// atualizado — sem precisar descer de novo nos casos em que nada mudou.
    /// </summary>
    public static Veredicto AvaliarComCache(string pasta)
    {
        long escrita;
        try { escrita = Directory.GetLastWriteTimeUtc(pasta).Ticks; }
        catch { return new Veredicto(false, "inacessivel"); }
        if (GameDatabase.LerVeredicto(pasta, escrita) is { } guardado)
            return new Veredicto(guardado.EhJogo, guardado.Motivo ?? "");
        var v = Avaliar(pasta);
        GameDatabase.GravarVeredicto(pasta, escrita, v.EhJogo, v.Motivo);
        return v;
    }

    public static Veredicto Avaliar(string pasta)
    {
        var nomeDaPasta = Path.GetFileName(Path.TrimEndingDirectorySeparator(pasta));
        if (NomesDeApp.IsMatch(nomeDaPasta)) return new Veredicto(false, $"pasta de aplicativo ({nomeDaPasta})");

        var pontos = 0;
        string? forte = null;
        string? contra = null;
        var arquivosDeDados = 0;
        var exes = new List<FileInfo>();
        var vistos = 0;

        try
        {
            var raiz = new DirectoryInfo(pasta);
            foreach (var d in raiz.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                if (PastasDeApp.Contains(d.Name)) contra ??= d.Name;
                // Unity: <Jogo>_Data ao lado do exe; Unreal: Engine\ + <Projeto>\Content\Paks
                if (d.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(Path.Combine(d.FullName, "globalgamemanagers"))
                        || File.Exists(Path.Combine(d.FullName, "data.unity3d"))))
                    forte ??= $"Unity ({d.Name})";
                if (d.Name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(d.FullName, "Binaries")))
                    forte ??= "Unreal Engine";
                if (d.Name.Equals(".egstore", StringComparison.OrdinalIgnoreCase)) forte ??= "manifesto da Epic";
                if (d.Name.Equals("renpy", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(pasta, "game"))) forte ??= "Ren'Py";
            }

            foreach (var f in raiz.EnumerateFiles("*", Busca))
            {
                if (++vistos > 20_000) break;   // pasta enorme: o que ja foi visto decide
                var nome = f.Name;
                if (MarcadoresFortes.Contains(nome)) forte ??= nome;
                else if (nome.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase)) forte ??= nome;
                if (Clientes.Contains(nome)) return new Veredicto(false, $"cliente de loja ({nome})");
                if (MarcadoresDeApp.Contains(nome)) contra ??= nome;

                var ext = f.Extension;
                if (ExtensoesDeDados.Contains(ext) && f.Length > 1024 * 1024) arquivosDeDados++;
                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (nome.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                        || nome.EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                        forte ??= nome;
                    if (ExeLocator.PareceExeDeJogo(f.FullName, nomeDaPasta)) exes.Add(f);
                }
                // Godot: <jogo>.exe com <jogo>.pck ao lado
                if (ext.Equals(".pck", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.ChangeExtension(f.FullName, ".exe")))
                    forte ??= "Godot";
            }
        }
        catch (Exception ex) { return new Veredicto(false, $"erro: {ex.Message}"); }

        if (exes.Count == 0) return new Veredicto(false, "nenhum executavel de jogo");
        if (forte is not null) return new Veredicto(true, forte);
        if (contra is not null) return new Veredicto(false, $"sinal de aplicativo ({contra})");

        if (arquivosDeDados >= 2) pontos += 40;
        else if (arquivosDeDados == 1) pontos += 20;

        // Os tres maiores executaveis: o jogo quase sempre e o maior, e ler import table custa.
        var importaGrafico = false;
        var importaEntrada = false;
        foreach (var exe in exes.OrderByDescending(e => e.Length).Take(3))
        {
            var pe = PeUtils.Inspect(exe.FullName);
            if (pe is null) continue;
            if (pe.Imports.Any(i => ApisGraficas.Any(a => i.StartsWith(a, StringComparison.Ordinal)))) importaGrafico = true;
            if (pe.Imports.Any(i => ApisDeEntrada.Any(a => i.StartsWith(a, StringComparison.Ordinal)))) importaEntrada = true;
            if (exe.Length > 8 * 1024 * 1024) pontos += 10;
        }
        if (importaGrafico) pontos += 30;
        if (importaEntrada) pontos += 30;
        if (importaGrafico && importaEntrada) pontos += 10;

        return pontos >= 60
            ? new Veredicto(true, $"evidencia {pontos} (grafico={importaGrafico}, entrada={importaEntrada}, dados={arquivosDeDados})")
            : new Veredicto(false, $"evidencia insuficiente ({pontos})");
    }

    // ---------- filtro das lojas ----------

    /// <summary>
    /// Entradas que as LOJAS registram e que nao sao jogo: runtimes, ferramentas, SDKs. A Steam
    /// instala o Proton e o Steam Linux Runtime como "apps" com pasta em steamapps\common, e eles
    /// apareciam na grade com bolinha de DLSS 5.
    /// </summary>
    private static readonly HashSet<string> SteamNaoJogos = new(StringComparer.Ordinal)
    {
        "228980",  // Steamworks Common Redistributables
        "1007",    // Steamworks SDK Redist
        "250820",  // SteamVR
        "1070560", "1391110", "1628350",  // Steam Linux Runtime (scout/soldier/sniper)
        // Proton: os numerados e o Experimental. Versoes novas caem no filtro por nome abaixo.
        "858280", "930400", "961940", "1054830", "1113280", "1245040", "1420170", "1493710",
        "1580130", "1887720", "2348590",
        "431960",  // Wallpaper Engine
        "365670",  // Blender
        "993090",  // Lossless Scaling
    };

    private static readonly Regex NomesNaoJogo = new(
        @"(^proton\b|steam linux runtime|steamworks|redistributable|\bsdk\b|dedicated server|"
        + @"soundtrack|\bost\b|artbook|wallpaper engine|steamvr|benchmark tool|mod tools?$|"
        + @"\beditor\b$|\bserver\b$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Uma entrada de loja deve ir para a grade? Exige ao menos um executavel na pasta —
    /// trilha sonora e DLC instalada como "app" nao tem — e descarta ferramentas conhecidas.</summary>
    public static bool EntradaDeLojaEhJogo(Models.GameInfo g, out string motivo)
    {
        if (g.Store == Models.GameStore.Steam && g.AppId is { } id && SteamNaoJogos.Contains(id))
        {
            motivo = $"ferramenta da Steam ({id})";
            return false;
        }
        if (NomesNaoJogo.IsMatch(g.Name))
        {
            motivo = $"nome de ferramenta ({g.Name})";
            return false;
        }
        try
        {
            var temExe = Directory.EnumerateFiles(g.InstallDir, "*.exe", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).Any();
            motivo = temExe ? "" : "pasta sem executavel";
            return temExe;
        }
        catch
        {
            // Pasta protegida (WindowsApps): a loja disse que e jogo, e nao temos como provar o
            // contrario.
            motivo = "";
            return true;
        }
    }
}
