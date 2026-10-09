using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace RenoDXLauncher.Services;

/// <summary>
/// O registro de UMA instalacao: o que o instalador disse que ia fazer, e o que de fato mudou no
/// disco.
///
/// As duas metades existem porque nenhuma basta sozinha. Os passos ("ReShade 6.x como dxgi.dll")
/// dizem a intencao; o observador de arquivos diz o resultado, inclusive o que algum servico
/// escreveu sem passar pelo BackupService — um `.pre-dxvk`, um `dlss5-feed.cfg` criado pelo
/// proprio addon, uma pasta `host64\` inteira. E e o resultado que a pessoa quer ver quando
/// pergunta "o que foi instalado aqui, exatamente?".
///
/// O que o observador ve tambem vai para o snapshot da pasta (SnapshotService.RegistrarTocados):
/// e essa lista que permite ao "restaurar" apagar arquivos que nenhum manifesto conhecia.
/// </summary>
public sealed class InstallLog : IDisposable
{
    public enum Tipo { Etapa, Criado, Alterado, Apagado, Renomeado, Aviso, Erro, Resumo }

    public sealed record Linha(DateTime Quando, Tipo Tipo, string Texto)
    {
        public string Marca => Tipo switch
        {
            Tipo.Criado => "+",
            Tipo.Alterado => "~",
            Tipo.Apagado => "-",
            Tipo.Renomeado => ">",
            Tipo.Aviso => "!",
            Tipo.Erro => "x",
            Tipo.Resumo => "=",
            _ => "•",
        };

        public override string ToString() => $"{Quando:HH:mm:ss}  {Marca}  {Texto}";
    }

    private readonly IProgress<Linha>? _destino;
    private readonly List<FileSystemWatcher> _observadores = [];
    private readonly ConcurrentDictionary<string, Tipo> _tocados = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Linha> _linhas = [];
    private readonly object _trava = new();
    private readonly string _raiz;

    public string Sessao { get; } = Guid.NewGuid().ToString("N")[..12];
    public IReadOnlyList<Linha> Linhas { get { lock (_trava) return _linhas.ToList(); } }

    /// <summary>Caminhos relativos a <see cref="_raiz"/> que esta instalacao tocou, com a primeira
    /// acao vista em cada um.</summary>
    public IReadOnlyDictionary<string, Tipo> Tocados => _tocados;

    /// <param name="raiz">A pasta de instalacao do jogo — tudo que o instalador escreve fica abaixo
    /// dela (o alvo do ReShade, o host64, o reparo do Streamline).</param>
    /// <param name="outras">Pastas fora da raiz que tambem serao escritas (alvo fora da pasta de
    /// instalacao, raro mas possivel com exe fixado a mao).</param>
    public InstallLog(string raiz, IEnumerable<string?> outras, IProgress<Linha>? destino)
    {
        _raiz = Path.GetFullPath(raiz);
        _destino = destino;
        foreach (var pasta in new[] { raiz }.Concat(outras).OfType<string>()
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(p => Directory.Exists(p)))
        {
            // A pasta extra so ganha observador proprio se nao estiver dentro da raiz.
            if (!pasta.Equals(_raiz, StringComparison.OrdinalIgnoreCase)
                && pasta.StartsWith(_raiz + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var w = new FileSystemWatcher(pasta)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                w.Created += (_, e) => Visto(e.FullPath, Tipo.Criado);
                w.Changed += (_, e) => Visto(e.FullPath, Tipo.Alterado);
                w.Deleted += (_, e) => Visto(e.FullPath, Tipo.Apagado);
                w.Renamed += (_, e) =>
                {
                    Visto(e.OldFullPath, Tipo.Apagado, silencioso: true);
                    Visto(e.FullPath, Tipo.Renomeado, de: e.OldName);
                };
                w.Error += (_, e) => Escrever(Tipo.Aviso, Localization.L.T("InstallLog_WatcherError", e.GetException().Message));
                w.EnableRaisingEvents = true;
                _observadores.Add(w);
            }
            catch (Exception ex) { Log.Warn($"install log: observar {pasta}: {ex.Message}"); }
        }
    }

    /// <summary>Um passo do instalador. Serve de <see cref="IProgress{T}"/> para os servicos.</summary>
    public void Etapa(string texto) => Escrever(Tipo.Etapa, texto);
    public void Aviso(string texto) => Escrever(Tipo.Aviso, texto);
    public void Erro(string texto) => Escrever(Tipo.Erro, texto);

    /// <summary>O progresso de texto que os servicos ja aceitam, encaminhado para o log.</summary>
    public IProgress<string> ComoProgresso(IProgress<string>? tambem) =>
        new ProgressoSincrono(s => { Etapa(s); tambem?.Report(s); });

    private void Escrever(Tipo tipo, string texto)
    {
        var l = new Linha(DateTime.Now, tipo, texto);
        lock (_trava) _linhas.Add(l);
        _destino?.Report(l);
    }

    private void Visto(string caminho, Tipo tipo, bool silencioso = false, string? de = null)
    {
        var rel = Relativo(caminho);
        if (rel is null || Ignorar(rel)) return;
        // A primeira acao e a que conta para o snapshot: um arquivo criado e depois alterado
        // continua sendo "criado por nos".
        var novo = _tocados.TryAdd(rel, tipo);
        if (!novo)
        {
            // Apagado e depois recriado no mesmo lugar e uma SUBSTITUICAO, nao uma criacao.
            if (_tocados[rel] == Tipo.Apagado && tipo is Tipo.Criado or Tipo.Renomeado)
                _tocados[rel] = Tipo.Alterado;
            return;
        }
        if (silencioso) return;
        Escrever(tipo, de is null ? rel : $"{rel}  {Localization.L.T("InstallLog_RenamedFrom", de)}");
    }

    /// <summary>O que nao interessa a ninguem: as copias que o proprio backup faz, temporarios de
    /// escrita atomica, e o diretorio de staging dos downloads.</summary>
    private static bool Ignorar(string rel) =>
        rel.StartsWith(BackupService.PastaDeBackup, StringComparison.OrdinalIgnoreCase)
        || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || rel.EndsWith(".parcial", StringComparison.OrdinalIgnoreCase);

    private string? Relativo(string caminho)
    {
        try
        {
            var rel = Path.GetRelativePath(_raiz, Path.GetFullPath(caminho));
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? caminho : rel;
        }
        catch { return null; }
    }

    /// <summary>
    /// Fecha a sessao: para de observar, descreve cada arquivo que ficou (tamanho, produto e
    /// versao quando e um binario) e grava tudo no banco.
    /// </summary>
    public void Fechar(string pastaDoJogo, bool ok)
    {
        // O FileSystemWatcher entrega em outra thread; um instante para os ultimos eventos.
        Thread.Sleep(150);
        foreach (var w in _observadores) w.EnableRaisingEvents = false;

        var criados = 0;
        var alterados = 0;
        var apagados = 0;
        foreach (var (rel, tipo) in _tocados.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var abs = Path.IsPathRooted(rel) ? rel : Path.Combine(_raiz, rel);
            if (Directory.Exists(abs)) continue;
            switch (tipo)
            {
                case Tipo.Apagado when !File.Exists(abs):
                    apagados++;
                    break;
                case Tipo.Criado or Tipo.Renomeado when File.Exists(abs):
                    criados++;
                    Escrever(Tipo.Resumo, $"{rel}  {Descrever(abs)}");
                    break;
                default:
                    if (!File.Exists(abs)) break;
                    alterados++;
                    Escrever(Tipo.Resumo, $"{rel}  {Descrever(abs)}  {Localization.L.T("InstallLog_Replaced")}");
                    break;
            }
        }
        Escrever(ok ? Tipo.Resumo : Tipo.Erro,
                 Localization.L.T(ok ? "InstallLog_Done" : "InstallLog_Interrupted", criados, alterados, apagados));

        GameDatabase.GravarLog(pastaDoJogo, Sessao, Linhas);
    }

    /// <summary>"5,2 MB · ReShade 6.4.1" — o suficiente para saber o que e o arquivo.</summary>
    internal static string Descrever(string caminho)
    {
        try
        {
            var fi = new FileInfo(caminho);
            var tamanho = fi.Length >= 1024 * 1024
                ? $"{fi.Length / 1024.0 / 1024.0:0.0} MB"
                : $"{Math.Max(1, fi.Length / 1024)} KB";
            var ext = fi.Extension.ToLowerInvariant();
            if (ext is ".dll" or ".exe" or ".addon64" or ".addon32" or ".asi")
            {
                var v = FileVersionInfo.GetVersionInfo(caminho);
                var produto = string.IsNullOrWhiteSpace(v.ProductName) ? v.FileDescription : v.ProductName;
                var versao = v.FileVersion?.Trim();
                if (!string.IsNullOrWhiteSpace(produto) || !string.IsNullOrWhiteSpace(versao))
                    return $"{tamanho} · {produto?.Trim()} {versao}".TrimEnd();
            }
            return tamanho;
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        foreach (var w in _observadores)
        {
            try { w.Dispose(); } catch { }
        }
        _observadores.Clear();
    }

    /// <summary>Progress&lt;T&gt; posta no contexto de quem o criou; aqui o relato tem de
    /// acontecer na hora, na thread de quem relata, para a ordem do log ser a ordem real.</summary>
    private sealed class ProgressoSincrono(Action<string> acao) : IProgress<string>
    {
        public void Report(string value) => acao(value);
    }
}
