using System;
using System.Collections.Generic;
using System.Text.Json;
using APIBack.Service;

namespace APIBack.Atendimento.Templates
{
    /// <summary>Categorias aceitas nesta primeira versao (autenticacao exige pipeline proprio da Meta e fica de fora).</summary>
    public static class CategoriaTemplate
    {
        public const string Utilidade = "UTILITY";
        public const string Marketing = "MARKETING";

        public static readonly IReadOnlyList<string> Todas = new[] { Utilidade, Marketing };

        /// <summary>Aceita o codigo da Meta ou o nome em portugues da tela.</summary>
        public static string? Normalizar(string? valor) => (valor ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "UTILITY" or "UTILIDADE" => Utilidade,
            "MARKETING" => Marketing,
            _ => null
        };
    }

    public static class IdiomaTemplate
    {
        public const string PortuguesBrasil = "pt_BR";

        /// <summary>Idiomas liberados. Para abrir outro idioma da Meta basta incluir aqui (o resto ja trata nome + idioma).</summary>
        public static readonly IReadOnlyList<string> Suportados = new[] { PortuguesBrasil };
    }

    public static class TipoBotao
    {
        public const string RespostaRapida = "QUICK_REPLY";
        public const string Url = "URL";
        public const string Telefone = "PHONE_NUMBER";
        public const string CopiarCodigo = "COPY_CODE";

        public static readonly IReadOnlyList<string> Todos = new[] { RespostaRapida, Url, Telefone, CopiarCodigo };
    }

    /// <summary>Estado LOCAL do envio. O status de aprovacao e sempre o da Meta (StatusMeta).</summary>
    public static class SituacaoTemplate
    {
        /// <summary>Gravado e a caminho da Meta. Bloqueia um segundo envio igual.</summary>
        public const string Enviando = "enviando";
        /// <summary>A Meta nao respondeu: pode ter criado ou nao. Nunca se reenvia sem antes conferir na Meta.</summary>
        public const string Verificando = "verificando";
        /// <summary>A Meta confirmou a criacao (tem ID da Meta); dai em diante vale o status_meta.</summary>
        public const string Enviado = "enviado";
        /// <summary>A Meta recusou o pedido (nada foi criado la). Fica no historico; pode-se enviar de novo.</summary>
        public const string Falha = "falha";
        public const string Excluido = "excluido";
    }

    /// <summary>Status que a Meta devolve para um modelo (campo status).</summary>
    public static class StatusMeta
    {
        public const string Aprovado = "APPROVED";
        public const string Pendente = "PENDING";
        public const string Rejeitado = "REJECTED";
        public const string Pausado = "PAUSED";
        public const string Desativado = "DISABLED";
        public const string EmRecurso = "IN_APPEAL";
        public const string ExclusaoPendente = "PENDING_DELETION";
        public const string Excluido = "DELETED";
        public const string Arquivado = "ARCHIVED";
        public const string LimiteExcedido = "LIMIT_EXCEEDED";
    }

    /// <summary>Resumo que a tela mostra (um so campo para filtrar e colorir).</summary>
    public static class StatusTemplate
    {
        public const string Enviando = "enviando";
        public const string Verificando = "verificando";
        public const string Falha = "falha";
        public const string EmAnalise = "em_analise";
        public const string Aprovado = "aprovado";
        public const string Rejeitado = "rejeitado";
        public const string Pausado = "pausado";
        public const string Desativado = "desativado";
        public const string Indisponivel = "indisponivel";
        public const string Excluido = "excluido";

        public static string De(string situacao, string? statusMeta) => situacao switch
        {
            SituacaoTemplate.Enviando => Enviando,
            SituacaoTemplate.Verificando => Verificando,
            SituacaoTemplate.Falha => Falha,
            SituacaoTemplate.Excluido => Excluido,
            _ => (statusMeta ?? string.Empty).ToUpperInvariant() switch
            {
                StatusMeta.Aprovado => Aprovado,
                StatusMeta.Pendente or StatusMeta.EmRecurso => EmAnalise,
                StatusMeta.Rejeitado => Rejeitado,
                StatusMeta.Pausado => Pausado,
                StatusMeta.Desativado => Desativado,
                _ => Indisponivel
            }
        };
    }

    /// <param name="Tipo">QUICK_REPLY, URL, PHONE_NUMBER ou COPY_CODE (codigos da Meta).</param>
    /// <param name="Url">Para URL: endereco https; pode terminar com {{1}} (parte variavel).</param>
    /// <param name="UrlExemplo">Para URL com {{1}}: o endereco completo de exemplo.</param>
    /// <param name="Codigo">Para COPY_CODE: codigo de exemplo (cupom).</param>
    public sealed record TemplateBotao(string? Tipo, string? Texto = null, string? Url = null, string? UrlExemplo = null, string? Telefone = null, string? Codigo = null);

    /// <summary>Conteudo do modelo no formato do Zippy (texto). Variaveis sao posicionais: {{1}}, {{2}}, ... com um exemplo cada.</summary>
    public sealed record TemplateConteudo(
        string? Cabecalho, IReadOnlyList<string>? CabecalhoExemplos,
        string? Corpo, IReadOnlyList<string>? CorpoExemplos,
        string? Rodape,
        IReadOnlyList<TemplateBotao>? Botoes);

    public sealed record CriarTemplateRequest(string? Nome, string? Categoria, string? Idioma, TemplateConteudo? Conteudo);

    /// <summary>Modelo ja validado e normalizado, pronto para ir a Meta.</summary>
    public sealed record TemplateNovo(string Nome, string Categoria, string Idioma, TemplateConteudo Conteudo);

    public sealed record ValidacaoTemplateView(bool Valido, Dictionary<string, List<string>> Erros, object? ComponentesMeta);

    public sealed record TemplateEventoView(string Acao, int? UsuarioId, DateTime CriadoEm, JsonElement? Detalhe);

    public sealed record TemplateView(
        Guid Id, Guid EstabelecimentoId, Guid? CanalId, string Nome, string Idioma, string Categoria, string? CategoriaMeta,
        TemplateConteudo Conteudo, string Status, string Situacao, string? StatusMeta, bool DisponivelParaEnvio,
        string? MotivoRejeicao, string? OrientacaoRejeicao, string? MetaTemplateId, string? Qualidade,
        string? UltimoErro, string? UltimoErroCodigo, int? CriadoPor, DateTime CriadoEm, DateTime? EnviadoEm,
        DateTime? StatusAlteradoEm, DateTime? SincronizadoEm, DateTime? ExcluidoEm,
        IReadOnlyList<TemplateEventoView>? Historico = null, JsonElement? ComponentesMeta = null);

    public sealed record ResumoTemplatesView(int Total, int Aprovados, int EmAnalise, int Rejeitados, int Outros);

    /// <param name="PodeGerenciar">Quem le pode criar, sincronizar e excluir (a tela esconde os botoes; o backend confere de novo).</param>
    public sealed record TemplatesListaView(
        Guid CanalId, string Origem, bool PodeGerenciar, IReadOnlyList<TemplateView> Itens, ResumoTemplatesView Resumo, DateTime? UltimaSincronizacao);

    /// <param name="Ignorados">Modelos da WABA que nao sao de nenhuma loja do Zippy (nunca alterados). So para a Gestao.</param>
    /// <param name="Completa">Toda a lista da Meta foi lida (todas as paginas).</param>
    public sealed record SincronizacaoView(
        int Atualizados, int Alterados, int NaoEncontrados, int? Ignorados, bool Completa, DateTime SincronizadoEm, TemplatesListaView Lista);

    /// <summary>Resposta da criacao. <paramref name="Confirmado"/> falso = a Meta nao respondeu; o modelo fica "verificando".</summary>
    public sealed record CriacaoTemplateView(bool Confirmado, TemplateView Modelo);

    public sealed record DiagnosticoTemplatesView(bool Ok, string Origem, string? Codigo, string Mensagem, string? ComoResolver, string? Link);

    /// <summary>Modelo do Zippy persistido (vinculo explicito loja + WABA + ID da Meta).</summary>
    public sealed class TemplateWhatsapp
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid? IdCanal { get; set; }
        public string WabaId { get; set; } = string.Empty;
        public string? MetaTemplateId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Idioma { get; set; } = IdiomaTemplate.PortuguesBrasil;
        public string Categoria { get; set; } = CategoriaTemplate.Utilidade;
        public string? CategoriaMeta { get; set; }
        public string ConteudoJson { get; set; } = "{}";
        public string? ComponentesMetaJson { get; set; }
        public string Situacao { get; set; } = SituacaoTemplate.Enviando;
        public string? StatusMeta { get; set; }
        public string? MotivoRejeicao { get; set; }
        public string? Qualidade { get; set; }
        public string? UltimoErro { get; set; }
        public string? UltimoErroCodigo { get; set; }
        public int? CriadoPor { get; set; }
        public DateTime? EnviadoEm { get; set; }
        public DateTime? StatusAlteradoEm { get; set; }
        public DateTime? SincronizadoEm { get; set; }
        public DateTime? ExcluidoEm { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    public sealed class TemplateEvento
    {
        public string Acao { get; set; } = string.Empty;
        public int? UsuarioId { get; set; }
        public string? DetalheJson { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    /// <summary>Nome + idioma (ou ID da Meta) ja usado nesta WABA.</summary>
    public sealed class TemplateConflitoException : Exception
    {
        public TemplateConflitoException(string message) : base(message) { }
    }

    /// <summary>Pedido malformado (falta o conteudo, categoria ou idioma desconhecidos): HTTP 400.</summary>
    public sealed class TemplateRequisicaoInvalidaException : RequestValidationException
    {
        public TemplateRequisicaoInvalidaException(Dictionary<string, List<string>> erros) : base("Dados invalidos.", erros) { }
    }

    /// <summary>Conteudo que a Meta recusaria (tamanho, variaveis, botoes): HTTP 422.</summary>
    public sealed class TemplateIncompativelException : RequestValidationException
    {
        public TemplateIncompativelException(Dictionary<string, List<string>> erros)
            : base("O modelo nao segue as regras da Meta. Corrija os campos indicados.", erros) { }
    }
}
