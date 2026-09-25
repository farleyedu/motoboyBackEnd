using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Atendimento
{
    public class HorarioDiaDto
    {
        /// <summary>0 = domingo ... 6 = sabado.</summary>
        public int Dia { get; set; }
        public string Abre { get; set; } = "11:00";
        public string Fecha { get; set; } = "23:00";
    }

    public class HorarioAtendimentoDto
    {
        public List<HorarioDiaDto> Dias { get; set; } = new();
    }

    public class AtendimentoConfigDto
    {
        public Guid EstabelecimentoId { get; set; }
        /// <summary>humano (padrao) ou ia (etapa 2).</summary>
        public string Modo { get; set; } = "humano";
        public string? SaudacaoHumano { get; set; }
        public string? MensagemForaHorario { get; set; }
        public HorarioAtendimentoDto? HorarioAtendimento { get; set; }
        /// <summary>O atendimento esta dentro do horario agora (sem horario configurado = sempre).</summary>
        public bool AbertoAgora { get; set; } = true;
        /// <summary>true quando o estabelecimento nunca salvou a configuracao (valem os padroes).</summary>
        public bool IsDefault { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
    }

    public class UpdateAtendimentoConfigRequest
    {
        public string? Modo { get; set; }
        public string? SaudacaoHumano { get; set; }
        public string? MensagemForaHorario { get; set; }
        public HorarioAtendimentoDto? HorarioAtendimento { get; set; }
    }

    public class RespostaRapidaDto
    {
        public Guid Id { get; set; }
        public string Titulo { get; set; } = string.Empty;
        public string? Atalho { get; set; }
        public string Texto { get; set; } = string.Empty;
        public int Ordem { get; set; }
        public bool Ativo { get; set; } = true;
    }

    public class SalvarRespostaRapidaRequest
    {
        public string? Titulo { get; set; }
        public string? Atalho { get; set; }
        public string? Texto { get; set; }
        public int Ordem { get; set; }
        public bool Ativo { get; set; } = true;
    }

    public class RenderRespostaRequest
    {
        public string? Texto { get; set; }
        public int? PedidoId { get; set; }
    }

    public class RenderRespostaResult
    {
        public string Texto { get; set; } = string.Empty;
        /// <summary>Variaveis sem valor (ficaram como estavam no texto).</summary>
        public List<string> Pendentes { get; set; } = new();
    }

    public class ConversaDoPedidoDto
    {
        public Guid? ConversaId { get; set; }
        public string? Estado { get; set; }
        public string? StatusAtendimento { get; set; }
        public int QtdNaoLidas { get; set; }
        public string? ClienteNome { get; set; }
        public string? Telefone { get; set; }
        /// <summary>Fim da janela de 24h do WhatsApp; fora dela so template aprovado.</summary>
        public DateTimeOffset? JanelaFimUtc { get; set; }
        public bool JanelaAberta { get; set; }
        /// <summary>true quando a conversa foi criada agora a partir do pedido.</summary>
        public bool Criada { get; set; }
    }

    public class MotoboyMessageDto
    {
        public long Id { get; set; }
        public int MotoboyId { get; set; }
        public int? PedidoId { get; set; }
        /// <summary>operator ou motoboy.</summary>
        public string Direction { get; set; } = "operator";
        public string Body { get; set; } = string.Empty;
        public string? QuickKey { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset? ReadAtUtc { get; set; }
    }

    public class SendMotoboyMessageRequest
    {
        public int? PedidoId { get; set; }
        public string? Body { get; set; }
        public string? QuickKey { get; set; }
    }

    public class MotoboyShortcutDto
    {
        public string Key { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}
