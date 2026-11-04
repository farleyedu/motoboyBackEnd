using System;
using System.Collections.Generic;
using APIBack.Service;

namespace APIBack.Acompanhamento.Fases
{
    /// <summary>
    /// Fases do acompanhamento (docs/crm-inteligente/01-backend-base-crm.md, zippy-admin, item 1). No quadro: conversa ... saindo.
    /// Fora dele: perdido, pausa (estado de quem opera) e encerrado.
    /// </summary>
    public static class FasesAcompanhamento
    {
        public const string Conversa = "conversa";
        public const string Proposta = "proposta";
        public const string Implantacao = "implantacao";
        public const string Teste = "teste";
        public const string Operando = "operando";
        public const string Saindo = "saindo";
        public const string Perdido = "perdido";
        public const string Pausa = "pausa";
        public const string Encerrado = "encerrado";

        public static readonly string[] NoQuadro = { Conversa, Proposta, Implantacao, Teste, Operando, Saindo };
        public static readonly string[] ForaDoQuadro = { Perdido, Pausa, Encerrado };
        public static readonly string[] Todas = { Conversa, Proposta, Implantacao, Teste, Operando, Saindo, Perdido, Pausa, Encerrado };

        public static bool Existe(string? fase) => fase != null && Array.IndexOf(Todas, fase) >= 0;

        /// <summary>Conquista: a oportunidade ainda nao tem loja.</summary>
        public static bool EmConquista(string fase) => fase is Conversa or Proposta;

        public static string Nome(string fase) => fase switch
        {
            Conversa => "Conversa",
            Proposta => "Proposta",
            Implantacao => "Implantação",
            Teste => "Teste",
            Operando => "Operando",
            Saindo => "Saindo",
            Perdido => "Perdido",
            Pausa => "Em pausa",
            Encerrado => "Encerrado",
            _ => fase
        };

        /// <summary>
        /// Fases compativeis com a situacao comercial da Gestao (fonte unica). Implantacao e teste sao as mesmas; operando, saindo e
        /// pausa sao "ativo"; encerrado e perdido sao "bloqueado" (conversa e proposta tambem, quando a loja voltou a conversar).
        /// </summary>
        public static IReadOnlyCollection<string> CompativeisCom(string situacao) => situacao switch
        {
            SituacoesComerciais.Implantacao => new[] { Implantacao },
            SituacoesComerciais.Teste => new[] { Teste },
            SituacoesComerciais.Ativo => new[] { Operando, Saindo, Pausa },
            SituacoesComerciais.Bloqueado => new[] { Encerrado, Perdido, Conversa, Proposta },
            _ => Todas
        };

        /// <summary>Fase que a situacao impoe quando a gravada nao combina mais (ativacao pelo pagamento, API legada, "comecar a operar").</summary>
        public static string PadraoDaSituacao(string situacao) => situacao switch
        {
            SituacoesComerciais.Implantacao => Implantacao,
            SituacoesComerciais.Teste => Teste,
            SituacoesComerciais.Ativo => Operando,
            SituacoesComerciais.Bloqueado => Encerrado,
            _ => Implantacao
        };

        /// <summary>
        /// Fase de uma oportunidade sem loja, pelo /crm legado: perdido; proposta (colunas proposta, quase, aguardando) so com envio
        /// registrado; senao conversa. Nulo = fechada (a fase vem da loja).
        /// </summary>
        public static string? FaseDaOportunidade(string? status, string? coluna, bool temEnvioRegistrado) => status switch
        {
            "perdido" => Perdido,
            "fechado" => null,
            _ => temEnvioRegistrado && coluna is "proposta" or "quase" or "aguardando" ? Proposta : Conversa
        };

        /// <summary>Nulo = a fase gravada continua valendo; senao, a fase para a qual a situacao da loja levou.</summary>
        public static string? Reconciliar(string faseGravada, string? situacaoLoja)
        {
            if (string.IsNullOrWhiteSpace(situacaoLoja)) return null;
            foreach (var fase in CompativeisCom(situacaoLoja))
                if (fase == faseGravada) return null;
            return PadraoDaSituacao(situacaoLoja);
        }
    }

    /// <summary>Faixas do quadro (saude). As regras ficam no prompt de saude; aqui so o historico.</summary>
    public static class FaixasAcompanhamento
    {
        public const string Bem = "bem";
        public const string Atencao = "atencao";
        public const string PrecisaDeAjuda = "precisa_ajuda";

        public static bool Existe(string? faixa) => faixa is Bem or Atencao or PrecisaDeAjuda;
    }

    /// <summary>Quem pode decidir uma passagem.</summary>
    public static class QuemDecide
    {
        public const string Voce = "voce";
        public const string SozinhoOuVoce = "sozinho_ou_voce";
    }

    public static class AutoresPassagem
    {
        public const string Usuario = "usuario";
        public const string Sistema = "sistema";
    }

    public static class CodigosAcompanhamento
    {
        public const string PassagemInexistente = "PASSAGEM_INEXISTENTE";
        public const string RequisitosPendentes = "REQUISITOS_PENDENTES";
        public const string FaseMudou = "FASE_MUDOU";
        public const string ChaveIdempotenciaObrigatoria = "CHAVE_IDEMPOTENCIA_OBRIGATORIA";
        public const string ChaveIdempotenciaReutilizada = "CHAVE_IDEMPOTENCIA_REUTILIZADA";
        public const string NaoEncontrado = "ACOMPANHAMENTO_NAO_ENCONTRADO";
        public const string FaseInvalida = "FASE_INVALIDA";
        public const string TesteObrigatorio = "TESTE_OBRIGATORIO";
        public const string PropostaNaoEncontrada = "PROPOSTA_NAO_ENCONTRADA";
        public const string PropostaMudou = "PROPOSTA_MUDOU";
        public const string PropostaNaoEditavel = "PROPOSTA_NAO_EDITAVEL";
        public const string PropostaForaDaFase = "PROPOSTA_FORA_DA_FASE";
        public const string PropostaSemOportunidade = "PROPOSTA_SEM_OPORTUNIDADE";
        public const string PropostaValorInvalido = "PROPOSTA_VALOR_INVALIDO";
        public const string VersaoObrigatoria = "VERSAO_OBRIGATORIA";
        public const string CanalInvalido = "CANAL_INVALIDO";
        public const string DestinatarioObrigatorio = "DESTINATARIO_OBRIGATORIO";
        public const string EnvioNoFuturo = "ENVIO_NO_FUTURO";
        public const string EvidenciaInvalida = "EVIDENCIA_INVALIDA";
        public const string RespostaInvalida = "RESPOSTA_INVALIDA";
    }

    /// <summary>Recusa de passagem ou conflito, com o corpo de "details" da resposta.</summary>
    public sealed class AcompanhamentoException : Exception
    {
        public AcompanhamentoException(int status, string code, string message, object? details = null) : base(message)
        {
            Status = status;
            Code = code;
            Details = details;
        }

        public int Status { get; }
        public string Code { get; }
        public object? Details { get; }
    }
}
