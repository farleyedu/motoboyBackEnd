using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace APIBack.Assistente;

/// <summary>
/// Cifra as chaves das IAs que a Gestão cadastra (CONTRATO-API §11). AES-GCM como <c>Atendimento/TokenProtector</c>, mas com subchave
/// própria derivada por HKDF de <c>Atendimento:ChaveToken</c> (não reaproveita a chave de cifra do WhatsApp). O id do provedor vai como
/// dado associado: a chave cifrada de um provedor não decifra como de outro.
/// </summary>
public sealed class CifraChavesAssistente
{
    public const string Prefixo = "ak1.";
    public const string Rotulo = "zippy-assistente-chaves-v1";
    private readonly byte[]? _chave;

    public CifraChavesAssistente(IConfiguration configuration)
    {
        var mestra = ChaveMestra(configuration["Atendimento:ChaveToken"]);
        if (mestra is not null) _chave = HKDF.DeriveKey(HashAlgorithmName.SHA256, mestra, 32, salt: [], info: Encoding.UTF8.GetBytes(Rotulo));
    }

    /// <summary>Mesma leitura da chave mestra do TokenProtector: base64 de 32 bytes ou qualquer frase (SHA-256).</summary>
    private static byte[]? ChaveMestra(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        try
        {
            var bytes = Convert.FromBase64String(texto);
            return bytes.Length == 32 ? bytes : SHA256.HashData(Encoding.UTF8.GetBytes(texto));
        }
        catch (FormatException)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(texto));
        }
    }

    public bool Configurado => _chave is not null;

    public string Proteger(string chave, string provedor)
    {
        if (_chave is null) throw new InvalidOperationException("Atendimento:ChaveToken não configurada.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var texto = Encoding.UTF8.GetBytes(chave);
        var cifrado = new byte[texto.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_chave, 16);
        aes.Encrypt(nonce, texto, cifrado, tag, Encoding.UTF8.GetBytes(provedor));
        var junto = new byte[nonce.Length + tag.Length + cifrado.Length];
        Buffer.BlockCopy(nonce, 0, junto, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, junto, nonce.Length, tag.Length);
        Buffer.BlockCopy(cifrado, 0, junto, nonce.Length + tag.Length, cifrado.Length);
        return Prefixo + Convert.ToBase64String(junto);
    }

    /// <summary>Decifra; qualquer problema (chave mestra trocada, outro provedor, texto mexido) = nulo.</summary>
    public string? Revelar(string? cifrado, string provedor)
    {
        if (string.IsNullOrWhiteSpace(cifrado) || _chave is null || !cifrado.StartsWith(Prefixo, StringComparison.Ordinal)) return null;
        try
        {
            var junto = Convert.FromBase64String(cifrado[Prefixo.Length..]);
            if (junto.Length < 28) return null;
            var texto = new byte[junto.Length - 28];
            using var aes = new AesGcm(_chave, 16);
            aes.Decrypt(junto[..12], junto[28..], junto[12..28], texto, Encoding.UTF8.GetBytes(provedor));
            return Encoding.UTF8.GetString(texto);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Os 4 últimos caracteres, para a tela reconhecer a chave sem vê-la.</summary>
    public static string Final(string chave) => chave.Length <= 4 ? new string('•', chave.Length) : chave[^4..];
}

// ============================================================================ banco

public sealed record ChaveGuardada(string Provedor, string Cifrada, string Final, string? SalvaPor, DateTimeOffset SalvaEm,
    DateTimeOffset? UltimoTesteEm, bool? UltimoTesteOk, string? UltimoTesteErro);

public interface IChavesIARepositorio
{
    Task<IReadOnlyList<ChaveGuardada>> Listar(CancellationToken ct);
    /// <summary>Grava (cria ou troca) e audita. Devolve a ação auditada: <c>cadastrou</c> ou <c>trocou</c>.</summary>
    Task<string> Salvar(string provedor, string cifrada, string final, int usuario, CancellationToken ct);
    Task<bool> Apagar(string provedor, int usuario, CancellationToken ct);
}

public sealed class ChavesIARepositorio(NpgsqlDataSource source) : IChavesIARepositorio
{
    public async Task<IReadOnlyList<ChaveGuardada>> Listar(CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        static DateTimeOffset? Utc(DateTime? d) => d is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
        var linhas = await c.QueryAsync<(string Provedor, string Cifrada, string Final, string? SalvaPor, DateTime SalvaEm, DateTime? TesteEm, bool? TesteOk, string? TesteErro)>(
            new CommandDefinition("""
                SELECT k.provedor, k.cifrada, k.final, COALESCE(u.nome, u.email, k.salva_por::text), k.salva_em,
                       k.ultimo_teste_em, k.ultimo_teste_ok, k.ultimo_teste_erro
                FROM assistente_chaves k LEFT JOIN usuario u ON u.id = k.salva_por
                """, cancellationToken: ct));
        return linhas.Select(l => new ChaveGuardada(l.Provedor, l.Cifrada, l.Final, l.SalvaPor, Utc(l.SalvaEm)!.Value, Utc(l.TesteEm), l.TesteOk, l.TesteErro)).ToList();
    }

    public async Task<string> Salvar(string provedor, string cifrada, string final, int usuario, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var existia = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM assistente_chaves WHERE provedor = @provedor)", new { provedor }, tx, cancellationToken: ct));
        var acao = existia ? "trocou" : "cadastrou";
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO assistente_chaves (provedor, cifrada, final, salva_por, salva_em, ultimo_teste_em, ultimo_teste_ok, ultimo_teste_erro)
            VALUES (@provedor, @cifrada, @final, @usuario, NOW(), NOW(), TRUE, NULL)
            ON CONFLICT (provedor) DO UPDATE SET cifrada = EXCLUDED.cifrada, final = EXCLUDED.final, salva_por = EXCLUDED.salva_por,
                salva_em = NOW(), ultimo_teste_em = NOW(), ultimo_teste_ok = TRUE, ultimo_teste_erro = NULL;
            INSERT INTO assistente_chaves_auditoria (provedor, acao, final, usuario) VALUES (@provedor, @acao, @final, @usuario);
            """, new { provedor, cifrada, final, usuario, acao }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return acao;
    }

    public async Task<bool> Apagar(string provedor, int usuario, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var final = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "DELETE FROM assistente_chaves WHERE provedor = @provedor RETURNING final", new { provedor }, tx, cancellationToken: ct));
        if (final is not null)
            await c.ExecuteAsync(new CommandDefinition(
                "INSERT INTO assistente_chaves_auditoria (provedor, acao, final, usuario) VALUES (@provedor, 'apagou', @final, @usuario)",
                new { provedor, final, usuario }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return final is not null;
    }
}

// ============================================================================ cache e leitura

/// <summary>
/// Chaves do banco já decifradas, em memória por até 1 minuto (cada instância do servidor; o que salvou invalida na hora). A leitura
/// é síncrona (o plano da cadeia pergunta "tem chave?" várias vezes); <see cref="Garantir"/> recarrega antes de usar.
/// </summary>
public sealed class ChavesIA(IServiceScopeFactory scopes, CifraChavesAssistente cifra, TimeProvider relogio, ILogger<ChavesIA> logger)
{
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(1);
    private volatile Foto _foto = new(DateTimeOffset.MinValue, new Dictionary<string, string>(), []);
    private readonly SemaphoreSlim _trava = new(1, 1);

    private sealed record Foto(DateTimeOffset Em, IReadOnlyDictionary<string, string> Chaves, IReadOnlyList<ChaveGuardada> Guardadas);

    public bool CifraDisponivel => cifra.Configurado;

    /// <summary>Chave do banco (decifrada) do provedor, pela última carga.</summary>
    public string? DoBanco(string provedor) => _foto.Chaves.GetValueOrDefault(provedor);

    /// <summary>Metadados do banco (sem valor) para a tela.</summary>
    public IReadOnlyList<ChaveGuardada> Guardadas => _foto.Guardadas;

    public void Invalidar() => _foto = _foto with { Em = DateTimeOffset.MinValue };

    /// <summary>Recarrega do banco se a última carga passou de 1 minuto. Falhou (tabela ainda não existe, banco fora): fica sem chave do banco.</summary>
    public async Task Garantir(CancellationToken ct)
    {
        if (relogio.GetUtcNow() - _foto.Em < Validade) return;
        await _trava.WaitAsync(ct);
        try
        {
            if (relogio.GetUtcNow() - _foto.Em < Validade) return;
            IReadOnlyList<ChaveGuardada> guardadas;
            try
            {
                using var scope = scopes.CreateScope();
                guardadas = await scope.ServiceProvider.GetRequiredService<IChavesIARepositorio>().Listar(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sem o valor nem a mensagem do banco no log.
                logger.LogWarning("Assistente: chaves do banco indisponíveis ({Erro}); seguindo só com as do ambiente.", ex.GetType().Name);
                guardadas = [];
            }
            var chaves = new Dictionary<string, string>();
            foreach (var g in guardadas)
            {
                if (cifra.Revelar(g.Cifrada, g.Provedor) is string valor) chaves[g.Provedor] = valor;
                else logger.LogWarning("Assistente: a chave guardada de {Provedor} não decifra (Atendimento:ChaveToken mudou?)", g.Provedor);
            }
            _foto = new Foto(relogio.GetUtcNow(), chaves, guardadas);
        }
        finally
        {
            _trava.Release();
        }
    }
}

public static class OrigensChave
{
    public const string Ambiente = "ambiente";
    public const string Banco = "banco";
    public const string Nenhuma = "nenhuma";
}

/// <summary>Qual chave vale para um provedor: variável de ambiente (se permitida e existir) ganha; senão a do banco. Público para teste.</summary>
public static class LeituraChave
{
    public static (string? Valor, string Origem) Resolver(DefinicaoProvedor d, Func<string, string?> ambiente, Func<string, string?> banco)
    {
        if (!string.IsNullOrWhiteSpace(d.VariavelChave) && ConfigIA.VariavelPermitida(d.VariavelChave))
        {
            var valor = ambiente(d.VariavelChave);
            if (!string.IsNullOrWhiteSpace(valor)) return (valor, OrigensChave.Ambiente);
        }
        var doBanco = banco(d.Id);
        return string.IsNullOrWhiteSpace(doBanco) ? (null, OrigensChave.Nenhuma) : (doBanco, OrigensChave.Banco);
    }
}

// ============================================================================ Gestão

public sealed record ChaveInfoDto(bool TemChave, string Origem, string? Final, string? SalvaPor, DateTimeOffset? SalvaEm,
    DateTimeOffset? UltimoTesteEm, bool? UltimoTesteOk, string? UltimoTesteErro);

/// <summary>§11.2 + §12.6: <c>Falha</c> = por que não gravou; <c>Aviso</c> = gravou, mas o modelo falhou (sucesso com ressalva).</summary>
public sealed record ResultadoChaveDto(string Provedor, bool Ok, bool Gravada, string? Final, string Origem, string? Erro, FalhaDto? Falha = null,
    FalhaDto? Aviso = null, IReadOnlyList<EtapaDiagnosticoDto>? Etapas = null);

public sealed record ChaveApagadaDto(string Provedor, bool Apagada);

public sealed class SalvarChaveRequest
{
    public string? Chave { get; set; }
}

/// <summary>Cadastrar, trocar e apagar chaves pela Gestão (CONTRATO-API §11 e §12.6). Nunca devolve nem loga o valor.</summary>
public sealed class GestaoChavesIA(IChavesIARepositorio repo, CifraChavesAssistente cifra, ChavesIA cache, DiagnosticoIA diagnostico,
    IFabricaProvedores fabrica, IEstadoCadeiaRepositorio estado, ILogger<GestaoChavesIA> logger)
{
    public const int MinTamanho = DiagnosticoIA.MinTamanho;
    public const int MaxTamanho = DiagnosticoIA.MaxTamanho;

    /// <summary>Falhou nessas: a chave não está provada e não é gravada.</summary>
    private static readonly HashSet<string> NaoGrava = [CodigosFalha.ChaveFormato, CodigosFalha.ChaveRecusada, CodigosFalha.SemConexao, CodigosFalha.UrlErrada, CodigosFalha.SemChave];

    public async Task<ResultadoChaveDto> Salvar(ConfigIA cfg, string provedor, string? chave, int usuario, CancellationToken ct)
    {
        var def = cfg.ProvedorDe(provedor) ?? throw new AssistenteException(404, "PROVEDOR_NAO_ENCONTRADO", "Esse provedor não está na configuração salva.");
        if (!cifra.Configurado)
            throw new AssistenteException(422, "CIFRA_INDISPONIVEL", "O servidor não tem Atendimento:ChaveToken: não dá para guardar chave cifrada.");
        var valor = DiagnosticoIA.Limpar(chave);
        var modelo = cfg.Cadeia.FirstOrDefault(i => i.Provedor == def.Id && i.Ligado && !string.IsNullOrWhiteSpace(i.Modelo))?.Modelo;
        var diag = await diagnostico.Diagnosticar(cfg, def.Id, modelo, valor, false, ct);
        if (!Provada(diag))
        {
            var falha = diag.Falha;
            logger.LogInformation("Assistente: chave nova de {Provedor} reprovada no teste ({Codigo} {Status} em {Etapa})", def.Id, falha?.Codigo, falha?.Status, falha?.Etapa);
            await cache.Garantir(ct);
            return new ResultadoChaveDto(def.Id, false, false, null, Origem(def), falha?.Titulo ?? "O provedor recusou a chave", falha, null, diag.Etapas);
        }

        var acao = await repo.Salvar(def.Id, cifra.Proteger(valor, def.Id), CifraChavesAssistente.Final(valor), usuario, ct);
        logger.LogInformation("Assistente: usuário {Usuario} {Acao} a chave de {Provedor}", usuario, acao, def.Id);
        cache.Invalidar();
        await cache.Garantir(ct);
        // Chave nova: os itens desse provedor desligados por "Chave recusada" voltam.
        foreach (var item in cfg.Cadeia.Where(i => i.Provedor == def.Id))
        {
            try { await estado.Religar(item.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Assistente: não religou {Item} ({Erro})", item.Id, ex.GetType().Name); }
        }
        // Provada pela lista, mas o modelo falhou: grava e avisa (o modelo se troca na tela).
        return new ResultadoChaveDto(def.Id, true, true, CifraChavesAssistente.Final(valor), Origem(def), null, null, diag.Ok ? null : diag.Falha, diag.Etapas);
    }

    /// <summary>
    /// A chave vale? Lista de modelos aceitou (todos menos OpenRouter, cuja lista é pública: lá a chamada prova, e falha na chamada que não
    /// é de chave também conta; sem item ligado com modelo, só o formato é conferido). Falhas de formato, chave, conexão ou endereço nunca
    /// gravam. Público para teste.
    /// </summary>
    public static bool Provada(DiagnosticoDto d)
    {
        if (d.Falha is { } f && NaoGrava.Contains(f.Codigo)) return false;
        var lista = d.Etapas.FirstOrDefault(e => e.Etapa == EtapasFalha.ListaModelos);
        if (lista is null || lista.Situacao is SituacoesEtapa.Falhou or SituacoesEtapa.Pulada) return false;
        if (!d.ListaPublica) return true;
        var chamada = d.Etapas.FirstOrDefault(e => e.Etapa == EtapasFalha.ChamadaModelo);
        return d.Ok || chamada?.Situacao == SituacoesEtapa.Falhou;
    }

    public async Task<ChaveApagadaDto> Apagar(string provedor, int usuario, CancellationToken ct)
    {
        var apagada = await repo.Apagar(provedor, usuario, ct);
        if (apagada) logger.LogInformation("Assistente: usuário {Usuario} apagou a chave de {Provedor}", usuario, provedor);
        cache.Invalidar();
        return new ChaveApagadaDto(provedor, apagada);
    }

    private string Origem(DefinicaoProvedor d) => fabrica.Descrever(d).Origem;

    /// <summary>O que a tela mostra por provedor (sem valor).</summary>
    public async Task<IReadOnlyDictionary<string, ChaveInfoDto>> Info(ConfigIA cfg, CancellationToken ct)
    {
        await cache.Garantir(ct);
        var guardadas = cache.Guardadas.ToDictionary(g => g.Provedor);
        return cfg.Definicoes.ToDictionary(d => d.Id, d =>
        {
            var (temChave, origem, final) = fabrica.Descrever(d);
            var g = guardadas.GetValueOrDefault(d.Id);
            return new ChaveInfoDto(temChave, origem, final ?? g?.Final, g?.SalvaPor, g?.SalvaEm, g?.UltimoTesteEm, g?.UltimoTesteOk, g?.UltimoTesteErro);
        });
    }
}
