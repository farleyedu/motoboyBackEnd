using System;
using System.Collections.Generic;

namespace APIBack.AssistenteComercial.Inteligencia
{
    /// <summary>Configuracao (secao <c>CrmInteligencia</c>). Descontos e regras financeiras nao existem aqui: permanecem desativados.</summary>
    public sealed class InteligenciaOptions
    {
        public const string Secao = "CrmInteligencia";

        /// <summary>Menor amostra (oportunidades distintas) para citar abordagem comparavel ou percentual no coaching. Abaixo disso: "sem evidencia suficiente".</summary>
        public int AmostraMinima { get; set; } = 5;
        public int JanelaCoachingDias { get; set; } = 90;
        /// <summary>Quantos exemplos reais cada padrao mostra.</summary>
        public int MaxExemplos { get; set; } = 3;
    }

    public static class TipoObjecao
    {
        public const string Preco = "preco", Concorrente = "concorrente", Funcionalidade = "funcionalidade", Integracao = "integracao",
            Prazo = "prazo", Decisor = "decisor", Confianca = "confianca", Timing = "timing", Suporte = "suporte", Outro = "outro";
        public static readonly string[] Todos = { Preco, Concorrente, Funcionalidade, Integracao, Prazo, Decisor, Confianca, Timing, Suporte, Outro };
    }

    public static class EstadoObjecao
    {
        public const string Levantada = "levantada", Investigando = "investigando", Respondida = "respondida", Resolvida = "resolvida", Impeditiva = "impeditiva";
        public static readonly string[] Todos = { Levantada, Investigando, Respondida, Resolvida, Impeditiva };
    }

    public static class ImpactoObjecao
    {
        public const string Desconhecido = "desconhecido", Baixo = "baixo", Medio = "medio", Alto = "alto";
        public static readonly string[] Todos = { Desconhecido, Baixo, Medio, Alto };
    }

    public static class OrigemObjecao
    {
        public const string Manual = "manual", Audio = "audio";
    }

    /// <summary>declarada = alguem disse/viu; hipotese = suspeita ainda sem confirmacao (nunca vira barreira real).</summary>
    public static class CertezaObjecao
    {
        public const string Declarada = "declarada", Hipotese = "hipotese";
        public static readonly string[] Todos = { Declarada, Hipotese };
    }

    public static class RegraRecomendacao
    {
        public const string BarreiraReal = "barreira_real", Diagnostico = "diagnostico", CompromissoVencido = "compromisso_vencido", AbordagemComparavel = "abordagem_comparavel";
        public static int Prioridade(string regra) => regra switch { BarreiraReal => 1, Diagnostico => 2, CompromissoVencido => 3, _ => 4 };
    }

    public static class EstadoRecomendacao
    {
        public const string Sugerida = "sugerida", Aceita = "aceita", Rejeitada = "rejeitada", Executada = "executada";
    }

    public static class ReacaoRecomendacao
    {
        public static readonly string[] Todas = { "positiva", "neutra", "negativa", "sem_resposta" };
    }

    public static class ResultadoRecomendacao
    {
        public static readonly string[] Todos = { "avancou", "manteve", "recuou", "perdida", "indefinido" };
    }

    public static class CodigoInteligencia
    {
        public const string SemAcesso = "CRM_INTELIGENCIA_SEM_ACESSO";
        public const string NaoEncontrada = "OPORTUNIDADE_NAO_ENCONTRADA";
        public const string ObjecaoNaoEncontrada = "OBJECAO_NAO_ENCONTRADA";
        public const string RecomendacaoNaoEncontrada = "RECOMENDACAO_NAO_ENCONTRADA";
        public const string EvidenciaAusente = "EVIDENCIA_AUSENTE";
        public const string Invalido = "DADO_INVALIDO";
        public const string HipoteseComoFato = "HIPOTESE_NAO_PODE_SER_IMPEDITIVA";
        public const string MotivoAusente = "MOTIVO_DA_CORRECAO_AUSENTE";
        public const string Conflito = "VERSAO_DESATUALIZADA";
        public const string TransicaoInvalida = "TRANSICAO_INVALIDA";
        public const string ConsentimentoAusente = "CONSENTIMENTO_AUSENTE";
        public const string Indisponivel = "CATALOGO_INDISPONIVEL";
    }

    /// <summary>Erro de dominio com status HTTP e codigo estavel (403, 404, 409, 422, 503).</summary>
    public sealed class InteligenciaException : Exception
    {
        public InteligenciaException(int status, string codigo, string message, object? detalhes = null) : base(message)
        {
            Status = status; Codigo = codigo; Detalhes = detalhes;
        }
        public int Status { get; }
        public string Codigo { get; }
        public object? Detalhes { get; }
    }

    // ---- Persistencia ----

    public sealed class Objecao
    {
        public Guid Id { get; set; }
        public Guid OportunidadeId { get; set; }
        public string Tipo { get; set; } = TipoObjecao.Outro;
        public string Descricao { get; set; } = string.Empty;
        public string Evidencia { get; set; } = string.Empty;
        public string Origem { get; set; } = OrigemObjecao.Manual;
        public Guid? EntradaAudioId { get; set; }
        public string Certeza { get; set; } = CertezaObjecao.Declarada;
        public string Estado { get; set; } = EstadoObjecao.Levantada;
        public string Impacto { get; set; } = ImpactoObjecao.Desconhecido;
        public string? Abordagem { get; set; }
        public string? ModuloCitado { get; set; }
        public DateTime? RespostaPrometidaAte { get; set; }
        public DateTime OcorridaEm { get; set; }
        public int CriadoPor { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime AtualizadoEm { get; set; }
        public int Versao { get; set; }

        public Objecao Copia() => (Objecao)MemberwiseClone();
    }

    public sealed class Recomendacao
    {
        public Guid Id { get; set; }
        public Guid OportunidadeId { get; set; }
        public int? ResponsavelUsuarioId { get; set; }
        public string Regra { get; set; } = string.Empty;
        public int Prioridade { get; set; }
        public string ImpressaoDigital { get; set; } = string.Empty;
        public string Sugestao { get; set; } = string.Empty;
        public string Hipotese { get; set; } = string.Empty;
        public string Acao { get; set; } = string.Empty;
        public string EvidenciasJson { get; set; } = "[]";
        public string? RedacaoIa { get; set; }
        public string Estado { get; set; } = EstadoRecomendacao.Sugerida;
        public string? MotivoRejeicao { get; set; }
        public string? Reacao { get; set; }
        public string? Resultado { get; set; }
        public string? Observacao { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime AtualizadoEm { get; set; }
        public int Versao { get; set; }

        public Recomendacao Copia() => (Recomendacao)MemberwiseClone();
    }

    /// <summary>Linha crua de entrada de audio confirmada (proposta/resultado ainda em JSON).</summary>
    public sealed class EntradaAudioConfirmada
    {
        public Guid Id { get; set; }
        public int UsuarioId { get; set; }
        public string? PropostaJson { get; set; }
        public string? ResultadoJson { get; set; }
        public DateTime AtualizadaEm { get; set; }
    }

    /// <summary>Objecao de outra oportunidade, ja resolvida, com abordagem registrada (base das abordagens comparaveis).</summary>
    public sealed record ObjecaoHistorica(Guid ObjecaoId, Guid OportunidadeId, string Tipo, string Abordagem, string Evidencia, DateTime AtualizadaEm);

    /// <summary>Objecao de oportunidade de um responsavel (base do coaching).</summary>
    public sealed record ObjecaoDoResponsavel(Guid ObjecaoId, Guid OportunidadeId, string Tipo, string Estado, string Certeza, string Impacto, string Evidencia,
        string? Abordagem, DateTime OcorridaEm, DateTime? RespostaPrometidaAte);

    public sealed record RecomendacaoDoResponsavel(Guid RecomendacaoId, Guid OportunidadeId, string Regra, string Estado, string? Reacao, string? Resultado, DateTime CriadoEm);

    // ---- API ----

    public sealed class RegistrarObjecaoRequest
    {
        /// <summary>Ausente = nova objecao; presente = correcao (exige <see cref="MotivoCorrecao"/> e <see cref="VersaoEsperada"/>).</summary>
        public Guid? Id { get; set; }
        public string? Tipo { get; set; }
        public string? Descricao { get; set; }
        /// <summary>Obrigatoria: o que foi dito/visto que sustenta a objecao.</summary>
        public string? Evidencia { get; set; }
        public string? Certeza { get; set; }
        public string? Estado { get; set; }
        public string? Impacto { get; set; }
        public string? Abordagem { get; set; }
        /// <summary>Funcionalidade/modulo que o cliente pediu; e confrontado com o catalogo comercial real.</summary>
        public string? ModuloCitado { get; set; }
        public DateTime? RespostaPrometidaAte { get; set; }
        public DateTime? OcorridaEm { get; set; }
        public string? MotivoCorrecao { get; set; }
        public int? VersaoEsperada { get; set; }
    }

    public sealed record ObjecaoDto(Guid Id, Guid OportunidadeId, string Tipo, string Descricao, string Evidencia, string Origem, Guid? EntradaAudioId,
        string Certeza, string Estado, string Impacto, string? Abordagem, string? ModuloCitado, DateTime? RespostaPrometidaAte, DateTime OcorridaEm,
        DateTime AtualizadaEm, int Versao);

    public sealed record ListaObjecoesDto(IReadOnlyList<ObjecaoDto> Objecoes, int ImportadasDoAudio);

    public sealed record EvidenciaDto(string Tipo, string Referencia, string Texto);

    public sealed record RecomendacaoDto(Guid Id, Guid OportunidadeId, string Regra, int Prioridade, string Sugestao, string Hipotese, string Acao,
        IReadOnlyList<EvidenciaDto> Evidencias, string? RedacaoIa, string Estado, string? MotivoRejeicao, string? Reacao, string? Resultado,
        string? Observacao, DateTime CriadoEm, DateTime AtualizadoEm, int Versao, string Natureza);

    public sealed class FeedbackRequest
    {
        /// <summary>aceita | rejeitada | executada | reacao | resultado.</summary>
        public string? Evento { get; set; }
        public string? Motivo { get; set; }
        public string? Reacao { get; set; }
        public string? Resultado { get; set; }
        public string? Observacao { get; set; }
        public int? VersaoEsperada { get; set; }
    }

    // ---- Coaching ----

    public sealed record ExemploCoaching(Guid OportunidadeId, string Tipo, string Trecho);

    /// <summary>
    /// Padrao documentado. Quando <see cref="Suficiente"/> e false so ha contagem bruta (Amostra): nenhum percentual e calculado.
    /// <see cref="Percentual"/> e fracao real Observados/Amostra, nunca estimativa.
    /// </summary>
    public sealed record PadraoCoaching(string Id, string Titulo, string Descricao, bool Suficiente, int Observados, int Amostra, double? Percentual,
        IReadOnlyList<ExemploCoaching> Exemplos, string? Exercicio);

    public sealed record CoachingDto(int ResponsavelUsuarioId, int JanelaDias, int AmostraObjecoes, int AmostraRecomendacoes, int AmostraMinima,
        IReadOnlyList<PadraoCoaching> Padroes, string Aviso);
}
