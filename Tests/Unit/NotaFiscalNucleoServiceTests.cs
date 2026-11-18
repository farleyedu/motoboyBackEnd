using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Integrations.NotaFiscal.Nucleo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class NotaFiscalNucleoServiceTests
{
    private static readonly Guid Loja = Guid.NewGuid();
    private const string Chave = "tok-1234567890-ABCDEF";

    private sealed class ProtetorFalso : ITokenProtector
    {
        public bool Configurado { get; set; } = true;
        public string Proteger(string token) => "cifrado:" + new string(token.Reverse().ToArray());
        public string? Revelar(string? cifrado) =>
            cifrado != null && cifrado.StartsWith("cifrado:") ? new string(cifrado["cifrado:".Length..].Reverse().ToArray()) : null;
    }

    private sealed class RepoFalso : INotaFiscalRepositorio
    {
        public ConexaoRow? Conexao;
        public RegrasDto? Regras;
        public List<ProdutoFiscalDto> Produtos = new();
        public (string Status, string? Mensagem)? UltimoTeste;
        public ProdutoFiscalRequest? UltimoLote;
        public Task<ConexaoRow?> ObterConexaoAsync(Guid loja, CancellationToken ct) => Task.FromResult(Conexao);
        public Task GravarConexaoAsync(ConexaoRow linha, int usuarioId, CancellationToken ct) { Conexao = linha; return Task.CompletedTask; }
        public Task RemoverChaveAsync(Guid loja, int usuarioId, CancellationToken ct)
        {
            if (Conexao != null) { Conexao.ChaveCifrada = null; Conexao.Status = StatusConexao.NaoConectado; }
            return Task.CompletedTask;
        }
        public Task GravarResultadoTesteAsync(Guid loja, string status, string? mensagem, DateTime em, CancellationToken ct)
        {
            UltimoTeste = (status, mensagem);
            if (Conexao != null) { Conexao.Status = status; Conexao.Mensagem = mensagem; Conexao.UltimoTesteEm = em; }
            return Task.CompletedTask;
        }
        public Task<string?> ObterCnpjAsync(Guid loja, CancellationToken ct) => Task.FromResult<string?>("12345678000190");
        public Task<RegrasDto?> ObterRegrasAsync(Guid loja, CancellationToken ct) => Task.FromResult(Regras);
        public Task GravarRegrasAsync(Guid loja, RegrasDto regras, int usuarioId, CancellationToken ct) { Regras = regras; return Task.CompletedTask; }
        public Task<IReadOnlyList<ProdutoFiscalDto>> ListarProdutosAsync(Guid loja, CancellationToken ct) => Task.FromResult<IReadOnlyList<ProdutoFiscalDto>>(Produtos);
        public Task<bool> GravarProdutoAsync(Guid loja, Guid produtoId, ProdutoFiscalRequest valores, bool substituir, CancellationToken ct)
        {
            var p = Produtos.FirstOrDefault(x => x.ProdutoId == produtoId);
            if (p == null) return Task.FromResult(false);
            p.Ncm = valores.Ncm; p.Cfop = valores.Cfop; p.Csosn = valores.Csosn; p.Cst = valores.Cst; p.Origem = valores.Origem; p.Unidade = valores.Unidade;
            p.Completo = NotaFiscalRegras.ProdutoCompleto(p.Ncm, p.Cfop, p.Csosn, p.Cst);
            return Task.FromResult(true);
        }
        public Task<int> GravarLoteAsync(Guid loja, IReadOnlyList<Guid> produtoIds, ProdutoFiscalRequest valores, CancellationToken ct)
        {
            UltimoLote = valores;
            return Task.FromResult(produtoIds.Count);
        }
    }

    private sealed class FabricaFalsa : IEmissorFiscalFabrica
    {
        public IEmissorFiscal? Emissor;
        public IEmissorFiscal? Obter(string provedor) => Emissor;
    }

    private sealed class EmissorFalso : IEmissorFiscal
    {
        public Func<ConexaoFiscal, ResultadoTeste> Teste = _ => new ResultadoTeste(true, "Conexao ok.", Array.Empty<PassoDoTeste>());
        public ConexaoFiscal? Recebida;
        public Task<ResultadoTeste> TestarAsync(ConexaoFiscal conexao, CancellationToken ct) { Recebida = conexao; return Task.FromResult(Teste(conexao)); }
        public Task<ResultadoEmissao> EmitirNfceAsync(ConexaoFiscal conexao, NotaParaEmitir nota, CancellationToken ct) => throw new NotSupportedException();
        public Task<ResultadoEmissao> ConsultarAsync(ConexaoFiscal conexao, string referencia, CancellationToken ct) => throw new NotSupportedException();
        public Task<ResultadoCancelamento> CancelarAsync(ConexaoFiscal conexao, string referencia, string motivo, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (NotaFiscalNucleoService Servico, RepoFalso Repo, FabricaFalsa Fabrica) Criar(ProtetorFalso? protetor = null)
    {
        var repo = new RepoFalso();
        var fabrica = new FabricaFalsa();
        return (new NotaFiscalNucleoService(repo, protetor ?? new ProtetorFalso(), fabrica, NullLogger<NotaFiscalNucleoService>.Instance), repo, fabrica);
    }

    private static SalvarConexaoRequest Pedido(string? chave = Chave, string ambiente = Ambientes.Homologacao) => new()
    {
        Provedor = Provedores.Focus, Ambiente = ambiente, Regime = Regimes.Simples, Chave = chave,
        Confirmacoes = new ConfirmacoesDto { Certificado = true, Csc = true },
    };

    [Fact]
    public async Task ChaveEntraCifradaENuncaVoltaNaResposta()
    {
        var (s, repo, _) = Criar();
        var dto = await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);

        Assert.True(dto.ChaveSalva);
        Assert.NotEqual(Chave, repo.Conexao!.ChaveCifrada);
        Assert.DoesNotContain(Chave, JsonSerializer.Serialize(dto));
        Assert.DoesNotContain(Chave, JsonSerializer.Serialize(await s.ObterConexaoAsync(Loja, default)));
    }

    [Fact]
    public async Task SalvarSemChaveMantemAQueJaExiste()
    {
        var (s, repo, _) = Criar();
        await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);
        var cifrada = repo.Conexao!.ChaveCifrada;

        var dto = await s.SalvarConexaoAsync(Loja, 7, Pedido(chave: null), default);

        Assert.Equal(cifrada, repo.Conexao!.ChaveCifrada);
        Assert.True(dto.ChaveSalva);
    }

    [Fact]
    public async Task ChaveNovaOuAmbienteNovoZeraOTeste()
    {
        var (s, repo, fabrica) = Criar();
        fabrica.Emissor = new EmissorFalso();
        await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);
        await s.TestarAsync(Loja, default);
        Assert.Equal(StatusConexao.Conectado, repo.Conexao!.Status);

        await s.SalvarConexaoAsync(Loja, 7, Pedido(chave: null, ambiente: Ambientes.Producao), default);

        Assert.Equal(StatusConexao.NaoConectado, repo.Conexao!.Status);
        Assert.Null(repo.Conexao.UltimoTesteEm);
    }

    [Fact]
    public async Task SalvarSoOsDadosDaEmpresaNaoDerrubaAConexaoTestada()
    {
        var (s, repo, fabrica) = Criar();
        fabrica.Emissor = new EmissorFalso();
        await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);
        await s.TestarAsync(Loja, default);

        await s.SalvarConexaoAsync(Loja, 7, Pedido(chave: null), default);

        Assert.Equal(StatusConexao.Conectado, repo.Conexao!.Status);
    }

    [Fact]
    public async Task SemChaveMestraNaoSalvaChave()
    {
        var (s, _, _) = Criar(new ProtetorFalso { Configurado = false });
        var ex = await Assert.ThrowsAsync<NotaFiscalException>(() => s.SalvarConexaoAsync(Loja, 7, Pedido(), default));
        Assert.Equal("NF_NAO_CONFIGURADO", ex.Codigo);
    }

    [Fact]
    public async Task ProvedorOuAmbienteInvalidoEhRecusado()
    {
        var (s, _, _) = Criar();
        var ruim = Pedido(); ruim.Provedor = "outro";
        await Assert.ThrowsAsync<NotaFiscalException>(() => s.SalvarConexaoAsync(Loja, 7, ruim, default));
        var ruim2 = Pedido(); ruim2.Ambiente = "pre";
        await Assert.ThrowsAsync<NotaFiscalException>(() => s.SalvarConexaoAsync(Loja, 7, ruim2, default));
    }

    [Fact]
    public async Task TestarSemChaveAvisaParaColar()
    {
        var (s, _, _) = Criar();
        var r = await s.TestarAsync(Loja, default);
        Assert.False(r.Ok);
        Assert.Contains("Cole a chave", r.Mensagem);
    }

    [Fact]
    public async Task TestarSemEmissorDizQueOProvedorAindaNaoEstaDisponivel()
    {
        var (s, repo, _) = Criar();
        await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);
        var r = await s.TestarAsync(Loja, default);
        Assert.False(r.Ok);
        Assert.Contains("ainda nao esta disponivel", r.Mensagem);
        Assert.Null(repo.UltimoTeste);   // nao marca erro por falta de implementacao
    }

    [Fact]
    public async Task TestarOkEntregaAChaveReveladaAoEmissorEMarcaConectado()
    {
        var (s, repo, fabrica) = Criar();
        var emissor = new EmissorFalso();
        fabrica.Emissor = emissor;
        await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);

        var r = await s.TestarAsync(Loja, default);

        Assert.True(r.Ok);
        Assert.Equal(Chave, emissor.Recebida!.Chave);
        Assert.Equal("12345678000190", emissor.Recebida.Cnpj);
        Assert.Equal(StatusConexao.Conectado, repo.UltimoTeste!.Value.Status);
        Assert.Equal(StatusConexao.Conectado, (await s.ObterConexaoAsync(Loja, default)).Status);
    }

    [Fact]
    public async Task TestarComFalhaMarcaErroComAMensagemDoEmissor()
    {
        var (s, repo, fabrica) = Criar();
        fabrica.Emissor = new EmissorFalso { Teste = _ => new ResultadoTeste(false, "Chave recusada pelo provedor.", Array.Empty<PassoDoTeste>()) };
        await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);

        var r = await s.TestarAsync(Loja, default);

        Assert.False(r.Ok);
        Assert.Equal(StatusConexao.Erro, repo.UltimoTeste!.Value.Status);
        Assert.Equal("Chave recusada pelo provedor.", repo.Conexao!.Mensagem);
    }

    [Fact]
    public async Task ExcecaoDoEmissorNaoVazaNemACaixaNemAChave()
    {
        var (s, repo, fabrica) = Criar();
        fabrica.Emissor = new EmissorFalso { Teste = c => throw new InvalidOperationException("Authorization: Basic " + c.Chave) };
        await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);

        var r = await s.TestarAsync(Loja, default);

        Assert.False(r.Ok);
        Assert.DoesNotContain(Chave, JsonSerializer.Serialize(r));
        Assert.DoesNotContain(Chave, repo.Conexao!.Mensagem ?? string.Empty);
    }

    [Fact]
    public async Task RemoverApagaAChaveEMantemORestante()
    {
        var (s, repo, _) = Criar();
        await s.SalvarConexaoAsync(Loja, 7, Pedido(), default);
        var dto = await s.RemoverConexaoAsync(Loja, 7, default);
        Assert.False(dto.ChaveSalva);
        Assert.Equal(Regimes.Simples, repo.Conexao!.Regime);
    }

    [Fact]
    public async Task ChecklistRefleteProdutosPendentes()
    {
        var (s, repo, _) = Criar();
        repo.Produtos.Add(new ProdutoFiscalDto { ProdutoId = Guid.NewGuid(), Nome = "Chope", Completo = false });
        repo.Produtos.Add(new ProdutoFiscalDto { ProdutoId = Guid.NewGuid(), Nome = "Pizza", Completo = true });
        var dto = await s.ObterConexaoAsync(Loja, default);
        Assert.False(dto.Checklist.First(i => i.Id == "produtos").Ok);
        Assert.Contains("1 produto esta", dto.Checklist.First(i => i.Id == "produtos").Dica);
    }

    [Fact]
    public async Task RegrasSemRegistroDevolvemOPadrao()
    {
        var (s, _, _) = Criar();
        var r = await s.ObterRegrasAsync(Loja, default);
        Assert.Equal(ModosEmissao.Manual, r.Modo);
    }

    [Fact]
    public async Task ListaDeProdutosFiltraPendentesEBusca()
    {
        var (s, repo, _) = Criar();
        repo.Produtos.Add(new ProdutoFiscalDto { ProdutoId = Guid.NewGuid(), Nome = "Chope claro", Categoria = "Bebidas", Completo = false });
        repo.Produtos.Add(new ProdutoFiscalDto { ProdutoId = Guid.NewGuid(), Nome = "Pizza", Categoria = "Pratos", Completo = true });

        Assert.Single(await s.ListarProdutosAsync(Loja, true, null, default));
        Assert.Single(await s.ListarProdutosAsync(Loja, false, "bebi", default));
        Assert.Equal(2, (await s.ListarProdutosAsync(Loja, false, null, default)).Count);
    }

    [Fact]
    public async Task ProdutoDeOutraLojaNaoEncontrado()
    {
        var (s, _, _) = Criar();
        var ex = await Assert.ThrowsAsync<NotaFiscalException>(() =>
            s.SalvarProdutoAsync(Loja, Guid.NewGuid(), new ProdutoFiscalRequest { Ncm = "22021000" }, default));
        Assert.Equal("NF_NAO_ENCONTRADO", ex.Codigo);
    }

    [Fact]
    public async Task ProdutoFicaCompletoComNcmCfopECsosn()
    {
        var (s, repo, _) = Criar();
        var id = Guid.NewGuid();
        repo.Produtos.Add(new ProdutoFiscalDto { ProdutoId = id, Nome = "Chope" });
        var dto = await s.SalvarProdutoAsync(Loja, id, new ProdutoFiscalRequest { Ncm = "2203.00.00", Cfop = "5102", Csosn = "102" }, default);
        Assert.True(dto.Completo);
        Assert.Equal("22030000", dto.Ncm);
    }

    [Fact]
    public async Task LoteExigeProdutosEAlgumCampo()
    {
        var (s, _, _) = Criar();
        await Assert.ThrowsAsync<NotaFiscalException>(() => s.SalvarLoteAsync(Loja, new ProdutoFiscalLoteRequest(), default));
        await Assert.ThrowsAsync<NotaFiscalException>(() =>
            s.SalvarLoteAsync(Loja, new ProdutoFiscalLoteRequest { ProdutoIds = { Guid.NewGuid() } }, default));
    }

    [Fact]
    public async Task LoteNormalizaEValidaOsCampos()
    {
        var (s, repo, _) = Criar();
        var total = await s.SalvarLoteAsync(Loja, new ProdutoFiscalLoteRequest { ProdutoIds = { Guid.NewGuid(), Guid.NewGuid() }, Ncm = "2202.10.00" }, default);
        Assert.Equal(2, total);
        Assert.Equal("22021000", repo.UltimoLote!.Ncm);
        await Assert.ThrowsAsync<NotaFiscalException>(() =>
            s.SalvarLoteAsync(Loja, new ProdutoFiscalLoteRequest { ProdutoIds = { Guid.NewGuid() }, Ncm = "123" }, default));
    }

    [Fact]
    public async Task ParceiroDesligadoNaoCriaEmpresa()
    {
        var parceiro = new ProvedorParceiroDesligado();
        Assert.False(parceiro.Habilitado);
        var r = await parceiro.CriarEmpresaAsync(new DadosEmpresaParceiro("Loja", null, "1", null, null, null, null, null, null, null, null, null, null), default);
        Assert.False(r.Ok);
        Assert.Equal("PARCEIRO_NAO_HABILITADO", r.Codigo);
    }
}
