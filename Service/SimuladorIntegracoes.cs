using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Simulador;
using APIBack.Payments.MercadoPago;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace APIBack.Service
{
    public interface ISimuladorIntegracoes
    {
        Task<IReadOnlyList<SimIntegracaoDto>> ListAsync(Guid estabelecimentoId);
    }

    /// <summary>Estado das integracoes reais que o simulador usa (o que aparece como "Conectadas" no laboratorio).</summary>
    public sealed class SimuladorIntegracoes : ISimuladorIntegracoes
    {
        private readonly IWabaPhoneRepository _waba;
        private readonly IConfiguration _configuration;
        private readonly IMercadoPagoAmbienteAtivo _payments;
        private readonly IPagamentoSegredoProtector _segredos;

        public SimuladorIntegracoes(IWabaPhoneRepository waba, IConfiguration configuration, IMercadoPagoAmbienteAtivo payments,
            IPagamentoSegredoProtector segredos)
        {
            _waba = waba;
            _configuration = configuration;
            _payments = payments;
            _segredos = segredos;
        }

        public async Task<IReadOnlyList<SimIntegracaoDto>> ListAsync(Guid estabelecimentoId)
        {
            string? phoneId = null;
            try { phoneId = await _waba.ObterPhoneNumberIdPorEstabelecimentoAsync(estabelecimentoId); }
            catch (Exception) { /* sem tabela/conexao: aparece como indisponivel abaixo */ }

            return new List<SimIntegracaoDto>
            {
                new()
                {
                    Chave = "whatsapp", Nome = "WhatsApp", Descricao = "Envio e recebimento de mensagens",
                    Status = string.IsNullOrWhiteSpace(phoneId) ? "atencao" : "ok",
                    Detalhe = string.IsNullOrWhiteSpace(phoneId)
                        ? "Este estabelecimento nao tem numero de WhatsApp configurado: o cliente simulado nao consegue mandar mensagem."
                        : "Numero configurado. As mensagens simuladas entram pelo webhook; nada sai para o WhatsApp real de clientes de teste."
                },
                new()
                {
                    Chave = "mapa", Nome = "Mapa / Geolocalizacao", Descricao = "Rotas e atualizacao de localizacao",
                    Status = string.IsNullOrWhiteSpace(_configuration["Google:MapsApiKey"]) ? "atencao" : "ok",
                    Detalhe = string.IsNullOrWhiteSpace(_configuration["Google:MapsApiKey"])
                        ? "Sem chave do Google Maps: o cardapio web nao consegue confirmar o endereco de entrega."
                        : "Geocodificador (Google) configurado."
                },
                new()
                {
                    Chave = "pagamento", Nome = "Mercado Pago", Descricao = "Pix e cartao do cardapio e do chat",
                    Status = _segredos.Configurado ? "ok" : "atencao",
                    Detalhe = _segredos.Configurado
                        ? $"Ambiente {_payments.Atual}. O simulador nao cobra: o pagamento e so um status."
                        : "Chave mestra de pagamentos (PAGAMENTOS_CHAVE_MESTRA) nao configurada."
                },
                new()
                {
                    Chave = "push", Nome = "Notificacoes push", Descricao = "Alertas e atualizacoes de status",
                    Status = "ok", Detalhe = "Tempo real (SignalR) ativo."
                }
            };
        }
    }
}
