using System.Diagnostics;
using System.IO;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Que tipo de jogo e este, e o que o "Ativar" vai por nele.
///
/// Todas as decisoes ja existiam no instalador — bitness, API, tradutor, DLSS proprio, Ponte,
/// Feeder, OptiScaler, host64 — mas so apareciam DEPOIS do clique, como passos, ou no
/// `dlss5 --check` do terminal. Aqui elas viram o cartao que a pessoa le ANTES: "Unreal Engine 5 ·
/// DX12 · 64 bits · DLSS 3.7 proprio → rota direta", e a lista do que entra na pasta.
///
/// Usa as mesmas funcoes que o instalador (Rotear, ApiDoExe, Applies de cada servico), nunca uma
/// copia delas: duas versoes da mesma regra ja divergiram neste projeto mais de uma vez.
/// </summary>
public static class GameProfileService
{
    public record Perfil(
        string? Motor,
        string Api,
        int? Bits,
        bool DlssProprio,
        string? DlssVersao,
        string? OutroUpscaler,
        string? AntiCheat,
        string Rota,
        IReadOnlyList<string> Plano,
        IReadOnlyList<string> Avisos)
    {
        /// <summary>"Unreal Engine · DX12 · 64 bits · DLSS 3.7" — a linha de resumo.</summary>
        public string Resumo => string.Join("  ·  ", new[]
        {
            Motor,
            Api,
            Bits is { } b ? L.T("Profile_Bits", b) : null,
            DlssProprio ? (DlssVersao is null ? L.T("Profile_NativeDlss") : L.T("Profile_NativeDlssVersion", DlssVersao))
                        : OutroUpscaler is not null ? L.T("Profile_OtherUpscaler", OutroUpscaler) : L.T("Profile_NoDlss"),
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    public static Perfil Detectar(GameInfo game, string targetDir, string? exe, bool forcarDgVoodoo = false)
    {
        var pe = exe is null ? null : PeUtils.Inspect(exe, readImports: false);
        int? bits = pe is null ? null : pe.Is64Bit ? 64 : 32;
        var api = Dlss5Installer.ApiDoExe(exe);
        var det = NeuralUpliftService.Detect(game.InstallDir, targetDir, null);
        var temDlss = det.HasDlss;
        var dlssVersao = VersaoDoDlss(game.InstallDir, targetDir);
        var anti = AntiCheatScanner.Detect(game.InstallDir, targetDir);

        var plano = new List<string>();
        var avisos = new List<string>();

        // --- tradutor ---
        var precisaDgVoodoo = DgVoodooService.Applies(exe);
        var d3d10 = DxvkService.AppliesD3d10(exe);
        var usarDxvk = d3d10 || (precisaDgVoodoo && bits == 32 && !forcarDgVoodoo && DxvkService.RecomendadoPara(exe));
        if (d3d10) plano.Add(L.T("Plan_DxvkD3d10", DxvkService.D3d10Version));
        else if (usarDxvk) plano.Add(L.T("Plan_Dxvk"));
        else if (precisaDgVoodoo) plano.Add(L.T("Plan_DgVoodoo"));

        // --- ReShade ---
        var vulkan = usarDxvk || (!precisaDgVoodoo && VulkanLayerService.Applies(exe));
        if (vulkan) plano.Add(L.T("Plan_ReShadeLayer"));
        else if (det.ReShadeDllName is { } dll) plano.Add(L.T("Plan_ReShadeKeep", dll));
        else plano.Add(L.T("Plan_ReShadeProxy", precisaDgVoodoo ? "dxgi.dll" : L.T("Plan_ReShadeProxyAuto")));

        // --- rota ---
        var alcancaD3d12 = exe is null || Dlss5Installer.ReachesD3D12(exe) || precisaDgVoodoo;
        var rota = Dlss5Installer.RotearPasta(targetDir, exe, temDlss);
        var feeder = rota.Feeder || (!temDlss && !rota.OptiScaler && FeederService.Applies(exe, temDlss, alcancaD3d12));
        string nomeDaRota;
        if (rota.Ponte && feeder) nomeDaRota = L.T("Route_MultiApi");
        else if (rota.Ponte) nomeDaRota = L.T("Route_Bridge");
        else if (rota.OptiScaler) nomeDaRota = L.T("Route_OptiScaler", rota.Upscaler ?? "FSR/XeSS");
        else if (feeder) nomeDaRota = L.T("Route_Feeder");
        else if (temDlss) nomeDaRota = L.T("Route_Direct");
        else nomeDaRota = L.T("Route_None");

        plano.Add(L.T("Plan_Addon", NeuralUpliftService.GenericAddonFile));
        plano.Add(L.T("Plan_Runtime", NeuralUpliftService.RuntimeFile, NeuralUpliftService.RayReconstructionFile));
        if (rota.Ponte) plano.Add(L.T("Plan_Bridge"));
        if (rota.OptiScaler) plano.Add(L.T("Plan_OptiScaler", rota.Upscaler ?? "FSR/XeSS"));
        if (feeder)
        {
            plano.Add(L.T("Plan_Feeder", FeederService.AddonFile, FeederService.FxFile));
            plano.Add(L.T("Plan_Lumenite"));
            if (FeederService.NeedsHost64(exe)) plano.Add(L.T("Plan_Host64", FeederService.Host64Dir, FeederService.Host64Exe));
        }
        plano.Add(L.T("Plan_Ini"));
        plano.Add(L.T("Plan_Hotkeys", ReShadeHotkeys.Descrever(ReShadeHotkeys.Atual().Overlay)));

        // --- avisos ---
        if (anti is not null) avisos.Add(L.T("Profile_Warn_AntiCheat", anti));
        if (FeederService.UsaDlss1(targetDir)) avisos.Add(L.T("Profile_Warn_Dlss1"));
        // Jogo Vulkan COM DLSS proprio tambem vai pelo Feeder (o addon so engancha o NGX de D3D12),
        // e dizer "o jogo nao tem DLSS" a quem esta vendo a opcao no menu dele seria falso.
        if (feeder) avisos.Add(L.T(temDlss ? "Profile_Warn_FeederVulkanDlss" : "Profile_Warn_FeederDlaa"));
        if (vulkan) avisos.Add(L.T("Profile_Warn_SmoothMotion"));
        var (branch, minor) = NeuralUpliftService.ProbeDriverVersion();
        // Relatado pelo autor do Feeder: o renodx-dlss5 4.6/4.7 falha a partir do 616.64. O build
        // embutido e o 4.70, entao o aviso vale enquanto a biblioteca tiver ele.
        if ((branch > 616 || (branch == 616 && minor >= 64)) && NeuralUpliftService.AddonDaBibliotecaEhOEmbutido)
            avisos.Add(L.T("Profile_Warn_DriverAddon", $"{branch}.{minor:00}"));
        if (det.Host.Blocker is { } bloqueio) avisos.Add(bloqueio);

        return new Perfil(DetectarMotor(game.InstallDir, exe), Dlss5Installer.ApiLabel(api), bits, temDlss, dlssVersao,
                          rota.Upscaler, anti, nomeDaRota, plano, avisos);
    }

    /// <summary>O motor do jogo, pelo que ele deixa na pasta. Null quando nao ha sinal claro.</summary>
    public static string? DetectarMotor(string installDir, string? exe)
    {
        try
        {
            bool Existe(params string[] partes) => File.Exists(Path.Combine([installDir, .. partes]));
            bool Pasta(params string[] partes) => Directory.Exists(Path.Combine([installDir, .. partes]));
            var nomeExe = exe is null ? "" : Path.GetFileName(exe);

            if (nomeExe.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase) || Pasta("Engine", "Binaries"))
                return "Unreal Engine";
            if (Existe("UnityPlayer.dll") || Existe("GameAssembly.dll")) return "Unity";
            if (exe is not null && File.Exists(Path.ChangeExtension(exe, ".pck"))) return "Godot";
            if (Directory.EnumerateFiles(installDir, "re_chunk_000.pak*", SearchOption.TopDirectoryOnly).Any()) return "RE Engine";
            if (Pasta("Bin64") && Directory.EnumerateFiles(Path.Combine(installDir, "Bin64"), "Cry*.dll").Any()) return "CryEngine";
            if (Pasta("Data") && (Directory.EnumerateFiles(Path.Combine(installDir, "Data"), "*.ba2").Any()
                                  || Directory.EnumerateFiles(Path.Combine(installDir, "Data"), "*.bsa").Any()))
                return "Creation Engine";
            if (Pasta("archive", "pc", "content")) return "REDengine";
            if (Directory.EnumerateFiles(installDir, "*_dir.vpk", new EnumerationOptions
                {
                    IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 2,
                }).Any()) return "Source";
            if (Pasta("base") && Directory.EnumerateFiles(Path.Combine(installDir, "base"), "*.resources").Any()) return "id Tech";
            if (Pasta("www", "js") && Existe("www", "js", "rpg_core.js")) return "RPG Maker MV";
            if (Existe("Game.rgss3a")) return "RPG Maker VX Ace";
            if (Pasta("renpy")) return "Ren'Py";
        }
        catch (Exception ex) { Log.Warn($"motor de {installDir}: {ex.Message}"); }
        return null;
    }

    /// <summary>A versao do nvngx_dlss.dll do proprio jogo ("3.7.10"), se houver.</summary>
    private static string? VersaoDoDlss(string installDir, string targetDir)
    {
        try
        {
            foreach (var raiz in new[] { targetDir, installDir }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var f = Directory.EnumerateFiles(raiz, "nvngx_dlss.dll", new EnumerationOptions
                {
                    IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 5,
                }).FirstOrDefault();
                if (f is null) continue;
                var v = FileVersionInfo.GetVersionInfo(f);
                return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
            }
        }
        catch { }
        return null;
    }
}
