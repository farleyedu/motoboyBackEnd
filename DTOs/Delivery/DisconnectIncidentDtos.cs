using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Delivery
{
    /// <summary>Acoes que a loja pode aplicar a um incidente de desconexao (campo tipoAcao).</summary>
    public static class IncidentActions
    {
        public const string AguardarContato = "aguardar_contato";
        public const string RegistrarContato = "registrar_contato";
        public const string ReofertarMesmoMotoboy = "reofertar_mesmo_motoboy";
        public const string Redistribuir = "redistribuir";
        public const string ConfirmarDevolucaoFisica = "confirmar_devolucao_fisica";
        public const string RegistrarFalha = "registrar_falha";
        public const string ConcluirManual = "concluir_manual";
        public const string Cancelar = "cancelar";

        public static readonly string[] IncidentLevel = { AguardarContato, RegistrarContato };
        public static readonly string[] ItemLevel =
            { ReofertarMesmoMotoboy, Redistribuir, ConfirmarDevolucaoFisica, RegistrarFalha, ConcluirManual, Cancelar };
        public static bool IsKnown(string? action) =>
            action != null && (Array.IndexOf(IncidentLevel, action) >= 0 || Array.IndexOf(ItemLevel, action) >= 0);
    }

    public sealed class IncidenteAtencaoListaDto
    {
        public DateTimeOffset ServerTimeUtc { get; set; }
        public IReadOnlyList<IncidenteAtencaoDto> Incidentes { get; set; } = Array.Empty<IncidenteAtencaoDto>();
    }

    public sealed class IncidenteAtencaoDto
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        /// <summary>aberto | aguardando_retorno | resolvido.</summary>
        public string Status { get; set; } = "aberto";
        /// <summary>Motivo real do encerramento do turno (inactivity_7h, client_end, explicit_switch, link_revoked...).</summary>
        public string Motivo { get; set; } = "";
        public string MotivoTexto { get; set; } = "";
        public DateTimeOffset AbertoEmUtc { get; set; }
        public DateTimeOffset TurnoEncerradoEmUtc { get; set; }
        public DateTimeOffset? ResolvidoEmUtc { get; set; }
        public string? ResolvidoPorNome { get; set; }
        public IncidenteMotoboyDto Motoboy { get; set; } = new();
        public IncidenteLocalizacaoDto? UltimaLocalizacao { get; set; }
        public IReadOnlyList<IncidentePedidoDto> Pedidos { get; set; } = Array.Empty<IncidentePedidoDto>();
        public IReadOnlyList<IncidenteAcaoDisponivelDto> AcoesIncidente { get; set; } = Array.Empty<IncidenteAcaoDisponivelDto>();
        public IReadOnlyList<IncidenteHistoricoDto> Historico { get; set; } = Array.Empty<IncidenteHistoricoDto>();
    }

    public sealed class IncidenteMotoboyDto
    {
        public int Id { get; set; }
        public string Nome { get; set; } = "";
        public string? Avatar { get; set; }
        /// <summary>Ja ha um turno aberto novo deste motoboy na loja.</summary>
        public bool Reconectado { get; set; }
        public DateTimeOffset? UltimoContatoUtc { get; set; }
        public DateTimeOffset? UltimaAtividadeUtc { get; set; }
    }

    public sealed class IncidenteLocalizacaoDto
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
        public double? AccuracyMeters { get; set; }
    }

    public sealed class IncidentePedidoDto
    {
        public int PedidoId { get; set; }
        public string Numero { get; set; } = "";
        public string? ClienteNome { get; set; }
        public string? Bairro { get; set; }
        public decimal? Valor { get; set; }
        public string? TipoPagamento { get; set; }
        /// <summary>Status atual do pedido, intocado (pendente, em_rota, atribuido...).</summary>
        public string StatusPedido { get; set; } = "";
        /// <summary>na_loja | coletado | em_rota.</summary>
        public string Estagio { get; set; } = "na_loja";
        public bool Coletado { get; set; }
        public DateTimeOffset? ColetadoEmUtc { get; set; }
        public bool DevolucaoConfirmada { get; set; }
        /// <summary>pendente | resolvido.</summary>
        public string Situacao { get; set; } = "pendente";
        public string? Resolucao { get; set; }
        public IReadOnlyList<IncidenteAcaoDisponivelDto> Acoes { get; set; } = Array.Empty<IncidenteAcaoDisponivelDto>();
    }

    public sealed class IncidenteAcaoDisponivelDto
    {
        public string Acao { get; set; } = "";
        public bool Permitida { get; set; }
        public string? MotivoBloqueio { get; set; }
        public bool ExigeJustificativa { get; set; }
        public bool ExigeConfirmacao { get; set; }
    }

    public sealed class IncidenteHistoricoDto
    {
        public long Id { get; set; }
        public string Acao { get; set; } = "";
        public int[] PedidoIds { get; set; } = Array.Empty<int>();
        public string? UsuarioNome { get; set; }
        public string? Justificativa { get; set; }
        public DateTimeOffset OcorridoEmUtc { get; set; }
    }

    public sealed class AcaoIncidenteRequest
    {
        public string? TipoAcao { get; set; }
        public List<int>? PedidoIds { get; set; }
        public int? MotoboyDestinoId { get; set; }
        public string? Motivo { get; set; }
        public int? VersaoEsperada { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    public sealed class AcaoIncidenteResultadoDto
    {
        public int PedidoId { get; set; }
        public bool Ok { get; set; }
        public string? Code { get; set; }
        public string? Mensagem { get; set; }
    }

    public sealed class AcaoIncidenteResponse
    {
        public IncidenteAtencaoDto Incidente { get; set; } = new();
        public IReadOnlyList<AcaoIncidenteResultadoDto> Resultados { get; set; } = Array.Empty<AcaoIncidenteResultadoDto>();
    }
}
