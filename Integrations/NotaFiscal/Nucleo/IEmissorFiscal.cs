using System.Threading;
using System.Threading.Tasks;

namespace APIBack.Integrations.NotaFiscal.Nucleo;

/// <summary>Contrato do emissor (provedor de nota). O prompt 2 implementa para o Focus NFe; aqui so a assinatura.</summary>
public interface IEmissorFiscal
{
    Task<ResultadoTeste> TestarAsync(ConexaoFiscal conexao, CancellationToken ct);
    Task<ResultadoEmissao> EmitirNfceAsync(ConexaoFiscal conexao, NotaParaEmitir nota, CancellationToken ct);
    Task<ResultadoEmissao> ConsultarAsync(ConexaoFiscal conexao, string referencia, CancellationToken ct);
    Task<ResultadoCancelamento> CancelarAsync(ConexaoFiscal conexao, string referencia, string motivo, CancellationToken ct);
}

/// <summary>Devolve null quando o provedor ainda nao tem implementacao.</summary>
public interface IEmissorFiscalFabrica
{
    IEmissorFiscal? Obter(string provedor);
}

/// <summary>Conta de parceiro do Zippy no provedor: cria as empresas das lojas por API. Desligada por padrao.</summary>
public interface IProvedorParceiro
{
    bool Habilitado { get; }
    Task<ResultadoEmpresa> CriarEmpresaAsync(DadosEmpresaParceiro dados, CancellationToken ct);
    Task<ResultadoEmpresa> AtualizarEmpresaAsync(string empresaIdNoProvedor, DadosEmpresaParceiro dados, CancellationToken ct);
    Task<ResultadoEmpresa> ConsultarEmpresaAsync(string empresaIdNoProvedor, CancellationToken ct);
    Task<ResultadoEmpresa> ExcluirEmpresaAsync(string empresaIdNoProvedor, CancellationToken ct);
}

/// <summary>Padrao no DI ate o prompt 2 registrar a implementacao real: sem provedor, sem parceiro.</summary>
public sealed class EmissorFiscalFabricaVazia : IEmissorFiscalFabrica
{
    public IEmissorFiscal? Obter(string provedor) => null;
}

public sealed class ProvedorParceiroDesligado : IProvedorParceiro
{
    private const string Mensagem = "O modo parceiro ainda nao esta habilitado.";
    private static readonly ResultadoEmpresa Desligado = new(false, null, null, null, "PARCEIRO_NAO_HABILITADO", Mensagem);

    public bool Habilitado => false;
    public Task<ResultadoEmpresa> CriarEmpresaAsync(DadosEmpresaParceiro dados, CancellationToken ct) => Task.FromResult(Desligado);
    public Task<ResultadoEmpresa> AtualizarEmpresaAsync(string empresaIdNoProvedor, DadosEmpresaParceiro dados, CancellationToken ct) => Task.FromResult(Desligado);
    public Task<ResultadoEmpresa> ConsultarEmpresaAsync(string empresaIdNoProvedor, CancellationToken ct) => Task.FromResult(Desligado);
    public Task<ResultadoEmpresa> ExcluirEmpresaAsync(string empresaIdNoProvedor, CancellationToken ct) => Task.FromResult(Desligado);
}
