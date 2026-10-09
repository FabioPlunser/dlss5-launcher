using System.IO;
using System.Windows.Input;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// As teclas do ReShade — abrir o overlay, ligar/desligar os efeitos, captura de tela...
///
/// O ReShade as guarda na secao [INPUT] do ReShade.ini de cada jogo, no formato
/// "codigo,ctrl,shift,alt" (codigo de tecla virtual do Windows e tres 0/1). Home abre o overlay
/// por padrao, e Home e tecla de jogo em muita coisa (camera, mapa, emulador); trocar exigia abrir
/// o overlay de cada jogo e refazer a escolha jogo por jogo.
///
/// Aqui a escolha e uma so, gravada no config do launcher, e vai para o ReShade.ini de toda
/// instalacao nova — e, pelo botao das Configuracoes, para os jogos que ja estao instalados.
/// </summary>
public static class ReShadeHotkeys
{
    /// <summary>Uma tecla com modificadores. Codigo 0 = nenhuma tecla (atalho desligado).</summary>
    public readonly record struct Atalho(int Vk, bool Ctrl, bool Shift, bool Alt)
    {
        public bool Vazio => Vk == 0;
        public string ParaIni() => $"{Vk},{(Ctrl ? 1 : 0)},{(Shift ? 1 : 0)},{(Alt ? 1 : 0)}";

        public static Atalho? DoIni(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return null;
            var p = valor.Split(',');
            if (p.Length < 1 || !int.TryParse(p[0].Trim(), out var vk)) return null;
            bool B(int i) => p.Length > i && p[i].Trim() == "1";
            return new Atalho(vk, B(1), B(2), B(3));
        }
    }

    /// <summary>Uma linha da tela: a chave no ini, o rotulo e a tecla padrao do ReShade.</summary>
    public record Definicao(string Chave, string RotuloKey, Atalho Padrao);

    /// <summary>
    /// As teclas que valem a pena expor. Os padroes sao os do proprio ReShade: Home para o
    /// overlay, Print Screen para captura, o resto desligado.
    /// </summary>
    public static readonly IReadOnlyList<Definicao> Definicoes =
    [
        new("KeyOverlay", "Hotkey_Overlay", new Atalho(0x24, false, false, false)),        // Home
        new("KeyEffects", "Hotkey_Effects", new Atalho(0, false, false, false)),
        new("KeyScreenshot", "Hotkey_Screenshot", new Atalho(0x2C, false, false, false)),  // Print Screen
        new("KeyReload", "Hotkey_Reload", new Atalho(0, false, false, false)),
        new("KeyPerformanceMode", "Hotkey_PerformanceMode", new Atalho(0, false, false, false)),
        new("KeyNextPreset", "Hotkey_NextPreset", new Atalho(0, false, false, false)),
        new("KeyPreviousPreset", "Hotkey_PreviousPreset", new Atalho(0, false, false, false)),
    ];

    public record Escolha(Atalho Overlay, IReadOnlyDictionary<string, Atalho> Todas);

    /// <summary>As teclas em vigor: a escolha do config, ou o padrao do ReShade.</summary>
    public static Escolha Atual(LauncherConfig? cfg = null)
    {
        cfg ??= LauncherConfig.Load();
        var todas = new Dictionary<string, Atalho>();
        foreach (var d in Definicoes)
            todas[d.Chave] = cfg.ReShadeKeys.TryGetValue(d.Chave, out var v) && Atalho.DoIni(v) is { } a ? a : d.Padrao;
        return new Escolha(todas["KeyOverlay"], todas);
    }

    /// <summary>
    /// Grava as teclas escolhidas no ReShade.ini. So as que a pessoa mudou: uma tecla que ela
    /// nunca tocou fica como o ReShade (ou ela mesma, pelo overlay) deixou.
    /// </summary>
    public static bool Aplicar(string iniPath, LauncherConfig? cfg = null, IProgress<string>? progress = null)
    {
        cfg ??= LauncherConfig.Load();
        if (cfg.ReShadeKeys.Count == 0) return false;
        try
        {
            var ini = new IniFile(iniPath);
            var mudou = false;
            foreach (var d in Definicoes)
            {
                if (!cfg.ReShadeKeys.TryGetValue(d.Chave, out var v) || Atalho.DoIni(v) is not { } a) continue;
                if (ini.Get("INPUT", d.Chave, ignoreCase: true) == a.ParaIni()) continue;
                ini.Set("INPUT", d.Chave, a.ParaIni());
                mudou = true;
            }
            if (!mudou) return false;
            ini.Save();
            progress?.Report(L.T("Hotkey_Applied", Descrever(Atual(cfg).Overlay)));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"teclas do ReShade em {iniPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Aplica em todos os ReShade.ini das pastas dadas — o da pasta do jogo e o do host64 dos
    /// jogos de 32 bits, que tem overlay proprio. Devolve quantos arquivos mudaram.
    /// </summary>
    public static int AplicarEmTodos(IEnumerable<string> pastas, LauncherConfig cfg)
    {
        var n = 0;
        foreach (var pasta in pastas.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(pasta)) continue;
                foreach (var ini in Directory.EnumerateFiles(pasta, "ReShade.ini", new EnumerationOptions
                         {
                             IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 5,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    if (ini.Contains(BackupService.PastaDeBackup, StringComparison.OrdinalIgnoreCase)) continue;
                    if (AddonService.IsGameRunning(Path.GetDirectoryName(ini)!)) continue;
                    if (Aplicar(ini, cfg)) n++;
                }
            }
            catch (Exception ex) { Log.Warn($"teclas em {pasta}: {ex.Message}"); }
        }
        return n;
    }

    /// <summary>"Ctrl+Shift+F2". Vazio vira "nenhuma".</summary>
    public static string Descrever(Atalho a)
    {
        if (a.Vazio) return L.T("Hotkey_None");
        var partes = new List<string>();
        if (a.Ctrl) partes.Add("Ctrl");
        if (a.Shift) partes.Add("Shift");
        if (a.Alt) partes.Add("Alt");
        partes.Add(NomeDaTecla(a.Vk));
        return string.Join("+", partes);
    }

    private static string NomeDaTecla(int vk)
    {
        var k = KeyInterop.KeyFromVirtualKey(vk);
        return k switch
        {
            Key.None => $"0x{vk:X2}",
            Key.Snapshot => "Print Screen",
            Key.Scroll => "Scroll Lock",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "Page Up",
            Key.Next => "Page Down",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Pause => "Pause",
            Key.Oem3 => "`",
            >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => $"Num {(int)(k - Key.NumPad0)}",
            _ => k.ToString(),
        };
    }

    /// <summary>A tecla que uma janela WPF acabou de receber, ja como atalho. Null quando so um
    /// modificador foi apertado (a pessoa ainda esta compondo o atalho).</summary>
    public static Atalho? DoTeclado(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        var mods = Keyboard.Modifiers;
        return new Atalho(vk, mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift),
                          mods.HasFlag(ModifierKeys.Alt));
    }
}
