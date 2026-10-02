using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.DTOs.Delivery;

namespace APIBack.Service
{
    /// <summary>Regras (puras, testaveis) da confirmacao do motoboy: oferta de rota, prazo e recusa.</summary>
    public static class OfertaRotaRules
    {
        /// <summary>Minutos que o motoboy tem para responder antes da oferta ser recusada sozinha.</summary>
        public const int PrazoPadraoMinutos = 2;
        public const int PrazoMaximoMinutos = 60;

        public const string MotivoSemResposta = "Sem resposta do motoboy no prazo.";

        public static bool IsValidPrazo(int minutos) => minutos is >= 1 and <= PrazoMaximoMinutos;

        /// <summary>A oferta passou do prazo sem resposta.</summary>
        public static bool Expirou(DateTimeOffset ofertadoEmUtc, DateTimeOffset agoraUtc, int prazoMinutos) =>
            ofertadoEmUtc.AddMinutes(prazoMinutos) <= agoraUtc;

        /// <summary>Instante em que a oferta vence (mostrado ao motoboy como contagem).</summary>
        public static DateTimeOffset ExpiraEm(DateTimeOffset ofertadoEmUtc, int prazoMinutos) => ofertadoEmUtc.AddMinutes(prazoMinutos);

        /// <summary>Monta a oferta pendente de uma fila: as paradas oferecidas e o prazo. Null quando nao ha oferta.</summary>
        public static MotoboyOfferDto? BuildOffer(IReadOnlyList<RouteStopDto> offeredStops, Guid? offerId, DateTimeOffset? offeredAtUtc, int prazoMinutos)
        {
            if (offeredStops.Count == 0 || offeredAtUtc is null) return null;
            return new MotoboyOfferDto
            {
                OfferId = offerId,
                OfferedAtUtc = offeredAtUtc.Value,
                ExpiresAtUtc = ExpiraEm(offeredAtUtc.Value, prazoMinutos),
                TimeoutMinutes = prazoMinutos,
                Stops = offeredStops.OrderBy(stop => stop.Position).ToList()
            };
        }
    }
}
