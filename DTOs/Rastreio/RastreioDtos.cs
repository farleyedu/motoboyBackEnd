using System;
using System.Collections.Generic;
using APIBack.Service;

namespace APIBack.DTOs.Rastreio
{
    public class AvisoDto
    {
        /// <summary>pedido_enviado_loja, pedido_confirmado_loja, saiu_da_loja, motoboy_chegando ou chegou.</summary>
        public string Tipo { get; set; } = string.Empty;
        /// <summary>pendente, enviada, falhou ou ignorada.</summary>
        public string Status { get; set; } = string.Empty;
        public string? Motivo { get; set; }
        public DateTimeOffset CriadaEm { get; set; }
        public DateTimeOffset? EnviadaEm { get; set; }
    }

    public class RastreioPedidoDto
    {
        public bool OptIn { get; set; }
        public List<AvisoDto> Avisos { get; set; } = new();
    }

    public class SetOptInRequest
    {
        public bool OptIn { get; set; }
    }

    public class AvisosConfigDto
    {
        public bool ReceivedEnabled { get; set; } = true;
        public bool AcceptedEnabled { get; set; } = true;
        public bool DispatchEnabled { get; set; } = true;
        public bool ArrivingEnabled { get; set; } = true;
        public int ArrivingMinutes { get; set; } = 5;
        public int ArrivingRadiusM { get; set; } = 400;
        public bool ArrivedEnabled { get; set; } = true;
        public bool ConfirmacaoAtendenteEnabled { get; set; } = true;
        public bool ProntoRetiradaEnabled { get; set; } = true;
        /// <summary>Texto de cada aviso (vazio = padrao). Variaveis: {cliente} {numero} {loja} {motoboy} {minutos} {link} {itens} {endereco} {total}.</summary>
        public string? TemplateReceived { get; set; }
        public string? TemplateAccepted { get; set; }
        public string? TemplateDispatch { get; set; }
        public string? TemplateArriving { get; set; }
        public string? TemplateArrived { get; set; }
        public string? TemplateConfirmacaoAtendente { get; set; }
        public string? TemplateProntoRetirada { get; set; }
        public string DefaultTemplateReceived { get; set; } = NoticeSettings.DefaultReceivedTemplate;
        public string DefaultTemplateAccepted { get; set; } = NoticeSettings.DefaultAcceptedTemplate;
        public string DefaultTemplateDispatch { get; set; } = NoticeSettings.DefaultDispatchTemplate;
        public string DefaultTemplateArriving { get; set; } = NoticeSettings.DefaultArrivingTemplate;
        public string DefaultTemplateArrived { get; set; } = NoticeSettings.DefaultArrivedTemplate;
        public string DefaultTemplateConfirmacaoAtendente { get; set; } = NoticeSettings.DefaultConfirmacaoAtendenteTemplate;
        public string DefaultTemplateProntoRetirada { get; set; } = NoticeSettings.DefaultProntoRetiradaTemplate;
    }

    public class UpdateAvisosConfigRequest
    {
        public bool ReceivedEnabled { get; set; } = true;
        public bool AcceptedEnabled { get; set; } = true;
        public bool DispatchEnabled { get; set; } = true;
        public bool ArrivingEnabled { get; set; } = true;
        public int? ArrivingMinutes { get; set; }
        public int? ArrivingRadiusM { get; set; }
        public bool ArrivedEnabled { get; set; } = true;
        public bool ConfirmacaoAtendenteEnabled { get; set; } = true;
        public bool ProntoRetiradaEnabled { get; set; } = true;
        public string? TemplateReceived { get; set; }
        public string? TemplateAccepted { get; set; }
        public string? TemplateDispatch { get; set; }
        public string? TemplateArriving { get; set; }
        public string? TemplateArrived { get; set; }
        public string? TemplateConfirmacaoAtendente { get; set; }
        public string? TemplateProntoRetirada { get; set; }
    }

    public class MotoboyPreferencesRequest
    {
        public bool CompartilharLocalizacaoCliente { get; set; }
    }

    public class MotoboyPreferencesDto
    {
        public bool CompartilharLocalizacaoCliente { get; set; }
    }

    /// <summary>Pedido em rota com opt-in que ainda pode receber aviso (linha do servico de avisos).</summary>
    public class NoticeCandidate
    {
        public int PedidoId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string? NomeCliente { get; set; }
        public string? Loja { get; set; }
        public int MotoboyId { get; set; }
        public string? MotoboyNome { get; set; }
        public bool MotoboyShares { get; set; }
        public bool DispatchDone { get; set; }
        public bool ArrivingDone { get; set; }
        public NoticeSettings Settings { get; set; } = new();
        public double? DistanceMeters { get; set; }
        public double? LocationAgeSeconds { get; set; }
        public double? SpeedMps { get; set; }
    }

    /// <summary>Dados brutos para montar o link publico (a regra do que aparece fica em PublicTrackingRules).</summary>
    public class PublicTrackingSource
    {
        public int PedidoId { get; set; }
        public bool Expired { get; set; }
        public int StatusPedido { get; set; }
        public DateTime? Previsao { get; set; }
        public double? DestLat { get; set; }
        public double? DestLon { get; set; }
        public string? Loja { get; set; }
        public double? LojaLat { get; set; }
        public double? LojaLon { get; set; }
        public string? MotoboyNome { get; set; }
        public bool MotoboyShares { get; set; }
        public double? MotoLat { get; set; }
        public double? MotoLon { get; set; }
        public double? LocationAgeSeconds { get; set; }
    }
}
