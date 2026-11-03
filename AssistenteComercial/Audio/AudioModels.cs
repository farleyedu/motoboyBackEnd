using System;
using System.Collections.Generic;

namespace APIBack.AssistenteComercial.Audio
{
    /// <summary>
    /// Configuracao do assistente de audio (secao <c>CrmAssistente:Audio</c>). Nada tem valor padrao de fornecedor, modelo, custo ou
    /// teto: enquanto qualquer campo obrigatorio faltar, o processamento externo fica desligado e o erro diz qual campo falta.
    /// </summary>
    public sealed class AssistenteAudioOptions
    {
        public const string Secao = "CrmAssistente:Audio";

        /// <summary>Chave geral: false = nenhum audio sai do servidor, mesmo com o resto configurado.</summary>
        public bool Habilitado { get; set; }

        /// <summary>Transcricao: hoje so "openai-compat" (POST multipart /audio/transcriptions, resposta verbose_json).</summary>
        public string? TranscricaoProvedor { get; set; }
        public string? TranscricaoEndpoint { get; set; }
        public string? TranscricaoModelo { get; set; }
        public string? TranscricaoChave { get; set; }

        /// <summary>Interpretacao do texto: hoje so "anthropic".</summary>
        public string? InterpretacaoProvedor { get; set; }
        public string? InterpretacaoModelo { get; set; }
        public string? InterpretacaoChave { get; set; }

        /// <summary>Tabela de custo na mesma unidade do teto (ex.: USD). Sem inventar: vazio = desligado.</summary>
        public decimal? CustoPorMinutoTranscricao { get; set; }
        public decimal? CustoPorMilTokensEntrada { get; set; }
        public decimal? CustoPorMilTokensSaida { get; set; }
        public decimal? TetoMensalCusto { get; set; }
        /// <summary>Pior caso de tokens da interpretacao, usado para reservar custo antes de chamar a IA.</summary>
        public int? MaxTokensSaida { get; set; }

        public int? MaxBytes { get; set; }
        public int? MaxDuracaoSegundos { get; set; }
        public int? TimeoutSegundos { get; set; }
        public int? MaxTentativas { get; set; }
        public int? RetencaoDias { get; set; }
        /// <summary>Atraso base entre tentativas (dobra a cada falha).</summary>
        public int AtrasoTentativaSegundos { get; set; } = 30;

        /// <summary>Campos obrigatorios que ainda faltam (nomes da configuracao). Vazio = pode processar.</summary>
        public IReadOnlyList<string> Faltando()
        {
            var f = new List<string>();
            void Texto(string? v, string n) { if (string.IsNullOrWhiteSpace(v)) f.Add(n); }
            void Num(decimal? v, string n) { if (v is null || v <= 0) f.Add(n); }
            void Int(int? v, string n) { if (v is null || v <= 0) f.Add(n); }
            Texto(TranscricaoProvedor, nameof(TranscricaoProvedor)); Texto(TranscricaoEndpoint, nameof(TranscricaoEndpoint));
            Texto(TranscricaoModelo, nameof(TranscricaoModelo)); Texto(TranscricaoChave, nameof(TranscricaoChave));
            Texto(InterpretacaoProvedor, nameof(InterpretacaoProvedor)); Texto(InterpretacaoModelo, nameof(InterpretacaoModelo));
            Texto(InterpretacaoChave, nameof(InterpretacaoChave));
            Num(CustoPorMinutoTranscricao, nameof(CustoPorMinutoTranscricao)); Num(CustoPorMilTokensEntrada, nameof(CustoPorMilTokensEntrada));
            Num(CustoPorMilTokensSaida, nameof(CustoPorMilTokensSaida)); Num(TetoMensalCusto, nameof(TetoMensalCusto));
            Int(MaxTokensSaida, nameof(MaxTokensSaida)); Int(MaxBytes, nameof(MaxBytes)); Int(MaxDuracaoSegundos, nameof(MaxDuracaoSegundos));
            Int(TimeoutSegundos, nameof(TimeoutSegundos)); Int(MaxTentativas, nameof(MaxTentativas)); Int(RetencaoDias, nameof(RetencaoDias));
            return f;
        }
    }

    public static class EstadoEntrada
    {
        public const string Recebido = "recebido";
        public const string Processando = "processando";
        public const string AguardandoRevisao = "aguardando_revisao";
        public const string Aplicando = "aplicando";
        public const string Confirmada = "confirmada";
        public const string Rejeitada = "rejeitada";
        public const string Falha = "falha";
        public const string Duplicada = "duplicada";
        /// <summary>Sem consentimento ou acima do teto: nada foi enviado a fornecedor externo.</summary>
        public const string Bloqueada = "bloqueada";
        public const string Expirada = "expirada";
    }

    public static class CodigoAudio
    {
        public const string NaoVerificado = "FUNCIONARIO_NAO_VERIFICADO";
        public const string NaoEncontrada = "ENTRADA_NAO_ENCONTRADA";
        public const string JaProcessada = "ENTRADA_JA_PROCESSADA";
        public const string Ambigua = "OPORTUNIDADE_AMBIGUA";
        public const string Incompleta = "PROPOSTA_INCOMPLETA";
        public const string IaIndisponivel = "IA_INDISPONIVEL";
        public const string ConsentimentoAusente = "CONSENTIMENTO_AUSENTE";
        public const string TetoExcedido = "TETO_MENSAL_EXCEDIDO";
        public const string OportunidadeAlterada = "OPORTUNIDADE_ALTERADA";
        public const string AlteracaoInvalida = "ALTERACAO_INVALIDA";
        public const string ChaveReutilizada = "CHAVE_REUTILIZADA";
        public const string MidiaInvalida = "MIDIA_INVALIDA";
        public const string SemAcesso = "OPORTUNIDADE_SEM_ACESSO";
    }

    /// <summary>Erro de dominio com status HTTP e codigo estavel (403, 404, 409, 422, 503).</summary>
    public sealed class AssistenteAudioException : Exception
    {
        public AssistenteAudioException(int status, string codigo, string message, object? detalhes = null, bool tentarDepois = false) : base(message)
        {
            Status = status; Codigo = codigo; Detalhes = detalhes; TentarDepois = tentarDepois;
        }
        public int Status { get; }
        public string Codigo { get; }
        public object? Detalhes { get; }
        /// <summary>Falha transitoria: o worker agenda nova tentativa.</summary>
        public bool TentarDepois { get; }
    }

    /// <summary>Tipos de alteracao que o assistente pode propor. Nada alem disto existe: plano, desconto, pagamento, cancelamento e exclusao nao tem tipo.</summary>
    public static class TipoAlteracao
    {
        /// <summary>Anotacao no historico da oportunidade (fato declarado, objecao, concorrente, preco citado, promessa).</summary>
        public const string NotaHistorico = "nota_historico";
        /// <summary>Texto de proxima acao da oportunidade.</summary>
        public const string ProximaAcao = "proxima_acao";
        public static readonly string[] Todos = { NotaHistorico, ProximaAcao };
    }

    // ---- Proposta revisavel (JSON em crm_assistente_entrada.proposta) ----

    /// <summary>Trecho literal da transcricao que sustenta um item. Sem trecho achado na transcricao, o item e descartado.</summary>
    public sealed record Evidencia(string Trecho);

    public sealed record ItemTexto(string Texto, string Trecho);
    public sealed record ItemNome(string Nome, string Trecho);
    public sealed record ItemPromessa(string Texto, string? Quem, string Trecho);
    public sealed record ItemPreco(string Texto, decimal? Valor, string Trecho);
    public sealed record ItemProximoPasso(string Texto, string? Data, string Trecho);
    public sealed record ItemDecisor(string Nome, string? Cargo, string Trecho);

    /// <summary>O que o vendedor DISSE. Nada aqui e deducao da IA.</summary>
    public sealed class FatosDeclarados
    {
        public ItemTexto? Empresa { get; set; }
        public ItemDecisor? Decisor { get; set; }
        public ItemPreco? Preco { get; set; }
        public List<ItemTexto> Objecoes { get; set; } = new();
        public List<ItemNome> Concorrentes { get; set; } = new();
        public List<ItemPromessa> Promessas { get; set; } = new();
        public ItemProximoPasso? ProximoPasso { get; set; }
    }

    /// <summary>O que a IA CONCLUIU sem o vendedor dizer. Nunca vira alteracao sozinho.</summary>
    public sealed record Inferencia(string Texto, string Confianca, string Trecho);

    /// <summary>Mudanca que so acontece se a pessoa aprovar. <see cref="Anterior"/> trava a aplicacao se o dado mudou no meio tempo.</summary>
    public sealed record AlteracaoProposta(string Id, string Tipo, string Valor, string? Anterior, string Trecho);

    /// <summary>Pedido que o audio fez e o sistema nao executa (plano, desconto, pagamento, cancelamento, exclusao...).</summary>
    public sealed record AcaoRecusada(string Tipo, string Motivo, string Trecho);

    public sealed record CandidatoOportunidade(Guid Id, string Empresa, string? Contato, int Pontos, IReadOnlyList<string> Motivos);

    public sealed class ResultadoMatch
    {
        /// <summary>unico | ambiguo | nenhum.</summary>
        public string Situacao { get; set; } = "nenhum";
        public Guid? OportunidadeId { get; set; }
        public List<CandidatoOportunidade> Candidatos { get; set; } = new();
    }

    public sealed class PropostaVisita
    {
        public int Versao { get; set; } = 1;
        public string? Resumo { get; set; }
        public FatosDeclarados Fatos { get; set; } = new();
        public List<Inferencia> Inferencias { get; set; } = new();
        public List<AlteracaoProposta> Alteracoes { get; set; } = new();
        public List<AcaoRecusada> AcoesRecusadas { get; set; } = new();
        public ResultadoMatch Oportunidade { get; set; } = new();
        /// <summary>Sinais de seguranca: tentativa_instrucao, evidencia_descartada, item_fora_do_contrato.</summary>
        public List<string> Sinais { get; set; } = new();
    }

    // ---- Persistencia / API ----

    public sealed class EntradaAudio
    {
        public Guid Id { get; set; }
        public string WaMensagemId { get; set; } = string.Empty;
        public int UsuarioId { get; set; }
        public Guid CanalId { get; set; }
        public Guid LojaGestaoId { get; set; }
        public string PhoneNumberId { get; set; } = string.Empty;
        public string NumeroCanalE164 { get; set; } = string.Empty;
        public string TelefoneE164 { get; set; } = string.Empty;
        public string? MidiaId { get; set; }
        public string? MidiaMime { get; set; }
        public string? MidiaHash { get; set; }
        public int? MidiaBytes { get; set; }
        public decimal? DuracaoSegundos { get; set; }
        public string Estado { get; set; } = EstadoEntrada.Recebido;
        public string? Etapa { get; set; }
        public string? ErroCodigo { get; set; }
        public string? ErroDetalhe { get; set; }
        public int Tentativas { get; set; }
        public DateTime? ProximaTentativaEm { get; set; }
        public string? Transcricao { get; set; }
        public string? PropostaJson { get; set; }
        public Guid? OportunidadeId { get; set; }
        public Guid? DuplicadaDe { get; set; }
        public decimal CustoEstimado { get; set; }
        public decimal CustoReal { get; set; }
        public string? ChaveConfirmacao { get; set; }
        public string? ResultadoJson { get; set; }
        public DateTime? RetornoEnviadoEm { get; set; }
        public DateTime RecebidaEm { get; set; }
        public DateTime CriadaEm { get; set; }
        public DateTime AtualizadaEm { get; set; }
        public int Versao { get; set; }
    }

    public sealed record EntradaResumoDto(
        Guid Id, string Estado, string? Etapa, string? ErroCodigo, string? ErroDetalhe, string? Resumo, string? Empresa, string SituacaoOportunidade,
        int Alteracoes, DateTime RecebidaEm, DateTime AtualizadaEm);

    public sealed record EntradaDetalheDto(
        Guid Id, string WaMensagemId, string Estado, string? Etapa, string? ErroCodigo, string? ErroDetalhe, int Tentativas, DateTime? ProximaTentativaEm,
        DateTime RecebidaEm, DateTime AtualizadaEm, decimal? DuracaoSegundos, decimal CustoEstimado, decimal CustoReal,
        string? TranscricaoOriginal, PropostaVisita? Proposta, Guid? OportunidadeId, Guid? DuplicadaDe, object? Resultado);

    public sealed class ConfirmarRequest
    {
        /// <summary>Obrigatorio quando a proposta e ambigua ou sem oportunidade; precisa ser uma oportunidade que o usuario pode acessar.</summary>
        public Guid? OportunidadeId { get; set; }
        /// <summary>Ids das alteracoes aprovadas. Ausente/vazio = confirmar sem aplicar nada.</summary>
        public List<string>? AlteracoesAprovadas { get; set; }
        /// <summary>Texto corrigido pela pessoa antes de aprovar (a edicao fica na auditoria).</summary>
        public List<EdicaoAlteracao>? Edicoes { get; set; }
    }

    public sealed class EdicaoAlteracao
    {
        public string Id { get; set; } = string.Empty;
        public string Valor { get; set; } = string.Empty;
    }

    public sealed class RejeitarRequest
    {
        public string? Motivo { get; set; }
    }

    public sealed record ResultadoAlteracao(string Id, string Tipo, string Situacao, string? Detalhe);
    public sealed record ResultadoConfirmacao(Guid EntradaId, Guid OportunidadeId, IReadOnlyList<ResultadoAlteracao> Alteracoes);

    public sealed record PreVisitaDto(Guid OportunidadeId, string Empresa, string? Contato, string? ProximaAcao, IReadOnlyList<string> Historico);

    /// <summary>Mensagem interna ja autorizada pelo pipeline do 07 (audio de funcionario verificado).</summary>
    public sealed record AudioRecebido(
        string WaMensagemId, int UsuarioId, Guid CanalId, Guid LojaGestaoId, string PhoneNumberId, string NumeroCanalE164, string TelefoneE164,
        string? MidiaId, string? Mime, DateTime RecebidaEmUtc);
}
