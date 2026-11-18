using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace APIBack.Integrations.NotaFiscal.Nucleo;

public interface INotaFiscalNucleoService
{
    IReadOnlyList<ProvedorInfo> CatalogoProvedores();
    Task<ConexaoDto> ObterConexaoAsync(Guid loja, CancellationToken ct);
    Task<ConexaoDto> SalvarConexaoAsync(Guid loja, int usuarioId, SalvarConexaoRequest request, CancellationToken ct);
    Task<ConexaoDto> RemoverConexaoAsync(Guid loja, int usuarioId, CancellationToken ct);
    Task<TesteConexaoDto> TestarAsync(Guid loja, CancellationToken ct);
    Task<RegrasDto> ObterRegrasAsync(Guid loja, CancellationToken ct);
    Task<RegrasDto> SalvarRegrasAsync(Guid loja, int usuarioId, RegrasDto regras, CancellationToken ct);
    Task<IReadOnlyList<ProdutoFiscalDto>> ListarProdutosAsync(Guid loja, bool pendentes, string? busca, CancellationToken ct);
    Task<ProdutoFiscalDto> SalvarProdutoAsync(Guid loja, Guid produtoId, ProdutoFiscalRequest valores, CancellationToken ct);
    Task<int> SalvarLoteAsync(Guid loja, ProdutoFiscalLoteRequest lote, CancellationToken ct);
}

public sealed class NotaFiscalNucleoService : INotaFiscalNucleoService
{
    private readonly INotaFiscalRepositorio _repo;
    private readonly ITokenProtector _protetor;
    private readonly IEmissorFiscalFabrica _fabrica;
    private readonly ILogger<NotaFiscalNucleoService> _log;
    private readonly TimeProvider _relogio;

    public NotaFiscalNucleoService(INotaFiscalRepositorio repo, ITokenProtector protetor, IEmissorFiscalFabrica fabrica,
        ILogger<NotaFiscalNucleoService> log, TimeProvider? relogio = null)
    {
        _repo = repo; _protetor = protetor; _fabrica = fabrica; _log = log; _relogio = relogio ?? TimeProvider.System;
    }

    public IReadOnlyList<ProvedorInfo> CatalogoProvedores() => NotaFiscalRegras.CatalogoProvedores();

    public async Task<ConexaoDto> ObterConexaoAsync(Guid loja, CancellationToken ct) =>
        await MontarDtoAsync(loja, await _repo.ObterConexaoAsync(loja, ct), ct);

    public async Task<ConexaoDto> SalvarConexaoAsync(Guid loja, int usuarioId, SalvarConexaoRequest r, CancellationToken ct)
    {
        if (r == null) throw NotaFiscalException.Invalida("Informe os dados da conexao.");
        if (!Provedores.Valido(r.Provedor)) throw NotaFiscalException.Invalida("Escolha um provedor da lista.");
        if (!Ambientes.Valido(r.Ambiente)) throw NotaFiscalException.Invalida("Escolha o ambiente: teste ou valendo.");
        if (r.Regime != null && !Regimes.Valido(r.Regime)) throw NotaFiscalException.Invalida("Regime invalido. Escolha MEI, Simples, Lucro presumido ou Lucro real.");

        var chave = NotaFiscalRegras.ValidarChave(r.Chave);
        var ie = NotaFiscalRegras.NormalizarInscricaoEstadual(r.InscricaoEstadual);
        if (chave != null && !_protetor.Configurado)
            throw NotaFiscalException.NaoConfigurado("A protecao de chaves do servidor nao esta configurada. Fale com o suporte.");

        var atual = await _repo.ObterConexaoAsync(loja, ct);
        var nova = atual ?? new ConexaoRow { EstabelecimentoId = loja };
        var mudouConexao = atual == null || chave != null || atual.Provedor != r.Provedor || atual.Ambiente != r.Ambiente;

        nova.Provedor = r.Provedor!;
        nova.Ambiente = r.Ambiente!;
        nova.Regime = r.Regime;
        nova.InscricaoEstadual = ie;
        nova.ConfirmouCertificado = r.Confirmacoes?.Certificado ?? false;
        nova.ConfirmouCsc = r.Confirmacoes?.Csc ?? false;
        if (chave != null) nova.ChaveCifrada = _protetor.Proteger(chave);
        if (mudouConexao)
        {
            nova.Status = StatusConexao.NaoConectado;
            nova.UltimoTesteEm = null;
            nova.Mensagem = null;
        }

        await _repo.GravarConexaoAsync(nova, usuarioId, ct);
        return await MontarDtoAsync(loja, nova, ct);
    }

    public async Task<ConexaoDto> RemoverConexaoAsync(Guid loja, int usuarioId, CancellationToken ct)
    {
        await _repo.RemoverChaveAsync(loja, usuarioId, ct);
        return await ObterConexaoAsync(loja, ct);
    }

    public async Task<TesteConexaoDto> TestarAsync(Guid loja, CancellationToken ct)
    {
        var linha = await _repo.ObterConexaoAsync(loja, ct);
        if (linha?.ChaveCifrada == null)
            return Falha("Cole a chave do provedor antes de testar.", "Chave do provedor", "Ainda nao ha chave salva.");

        var emissor = _fabrica.Obter(linha.Provedor);
        if (emissor == null)
            return Falha("Este provedor ainda nao esta disponivel.", "Provedor", "A emissao por este provedor chega em breve.");

        var chave = _protetor.Revelar(linha.ChaveCifrada);
        if (chave == null)
            return Falha("Nao foi possivel abrir a chave guardada. Cole-a de novo.", "Chave do provedor", "A chave salva nao pode ser lida.");

        var cnpj = await _repo.ObterCnpjAsync(loja, ct);
        ResultadoTeste resultado;
        try
        {
            resultado = await emissor.TestarAsync(new ConexaoFiscal(loja, linha.Provedor, linha.Ambiente, chave, cnpj, linha.Regime, linha.InscricaoEstadual), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Nunca loga a chave nem o texto da excecao do provedor, que pode ecoar o cabecalho.
            _log.LogWarning("[nf.teste] ev=erro loja={Loja} tipo={Tipo}", loja, ex.GetType().Name);
            resultado = new ResultadoTeste(false, "Nao foi possivel falar com o provedor agora. Tente de novo em instantes.", Array.Empty<PassoDoTeste>());
        }

        await _repo.GravarResultadoTesteAsync(loja, resultado.Ok ? StatusConexao.Conectado : StatusConexao.Erro,
            resultado.Ok ? null : resultado.Mensagem, _relogio.GetUtcNow().UtcDateTime, ct);
        return new TesteConexaoDto { Ok = resultado.Ok, Mensagem = resultado.Mensagem, Passos = resultado.Passos };
    }

    public async Task<RegrasDto> ObterRegrasAsync(Guid loja, CancellationToken ct) =>
        await _repo.ObterRegrasAsync(loja, ct) ?? NotaFiscalRegras.RegrasPadrao();

    public async Task<RegrasDto> SalvarRegrasAsync(Guid loja, int usuarioId, RegrasDto regras, CancellationToken ct)
    {
        var valida = NotaFiscalRegras.ValidarRegras(regras);
        await _repo.GravarRegrasAsync(loja, valida, usuarioId, ct);
        return valida;
    }

    public async Task<IReadOnlyList<ProdutoFiscalDto>> ListarProdutosAsync(Guid loja, bool pendentes, string? busca, CancellationToken ct)
    {
        IEnumerable<ProdutoFiscalDto> lista = await _repo.ListarProdutosAsync(loja, ct);
        if (pendentes) lista = lista.Where(p => !p.Completo);
        if (!string.IsNullOrWhiteSpace(busca))
        {
            var b = busca.Trim();
            lista = lista.Where(p => p.Nome.Contains(b, StringComparison.OrdinalIgnoreCase) ||
                                     (p.Categoria?.Contains(b, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return lista.ToList();
    }

    public async Task<ProdutoFiscalDto> SalvarProdutoAsync(Guid loja, Guid produtoId, ProdutoFiscalRequest valores, CancellationToken ct)
    {
        var v = NotaFiscalRegras.NormalizarProduto(valores ?? throw NotaFiscalException.Invalida("Informe os dados fiscais."));
        if (!await _repo.GravarProdutoAsync(loja, produtoId, v, true, ct))
            throw NotaFiscalException.NaoEncontrada("Produto nao encontrado nesta loja.");
        return (await _repo.ListarProdutosAsync(loja, ct)).First(p => p.ProdutoId == produtoId);
    }

    public async Task<int> SalvarLoteAsync(Guid loja, ProdutoFiscalLoteRequest lote, CancellationToken ct)
    {
        if (lote == null || lote.ProdutoIds.Count == 0) throw NotaFiscalException.Invalida("Escolha pelo menos um produto.");
        if (lote.ProdutoIds.Count > 500) throw NotaFiscalException.Invalida("Escolha no maximo 500 produtos por vez.");
        var v = NotaFiscalRegras.NormalizarProduto(new ProdutoFiscalRequest
        {
            Ncm = lote.Ncm, Cfop = lote.Cfop, Csosn = lote.Csosn, Cst = lote.Cst, Origem = lote.Origem, Unidade = lote.Unidade,
        });
        if (v.Ncm == null && v.Cfop == null && v.Csosn == null && v.Cst == null && v.Origem == null && v.Unidade == null)
            throw NotaFiscalException.Invalida("Preencha pelo menos um campo para aplicar aos produtos escolhidos.");
        return await _repo.GravarLoteAsync(loja, lote.ProdutoIds, v, ct);
    }

    // ---------- internos ----------

    private static TesteConexaoDto Falha(string mensagem, string passo, string detalhe) => new()
    {
        Ok = false, Mensagem = mensagem, Passos = new[] { new PassoDoTeste { Titulo = passo, Ok = false, Detalhe = detalhe } },
    };

    private async Task<ConexaoDto> MontarDtoAsync(Guid loja, ConexaoRow? linha, CancellationToken ct)
    {
        var pendentes = (await _repo.ListarProdutosAsync(loja, ct)).Count(p => !p.Completo);
        var status = linha?.Status ?? StatusConexao.NaoConectado;
        return new ConexaoDto
        {
            Provedor = linha?.Provedor,
            Ambiente = linha?.Ambiente ?? Ambientes.Homologacao,
            Status = status,
            Regime = linha?.Regime,
            InscricaoEstadual = linha?.InscricaoEstadual,
            ChaveSalva = linha?.ChaveCifrada != null,
            Confirmacoes = new ConfirmacoesDto { Certificado = linha?.ConfirmouCertificado ?? false, Csc = linha?.ConfirmouCsc ?? false },
            UltimoTesteEm = linha?.UltimoTesteEm,
            Mensagem = linha?.Mensagem,
            ModoConta = linha?.ModoConta ?? ModosConta.DaLoja,
            Checklist = NotaFiscalRegras.Checklist(linha?.Regime, linha?.ConfirmouCertificado ?? false, linha?.ConfirmouCsc ?? false,
                linha?.ChaveCifrada != null, status, pendentes),
        };
    }
}
