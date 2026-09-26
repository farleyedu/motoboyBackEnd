using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Simulador;
using APIBack.Payments.Options;
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
        private readonly IOptions<AsaasCheckoutOptions> _payments;

        public SimuladorIntegracoes(IWabaPhoneRepository waba, IConfiguration configuration, IOptions<AsaasCheckoutOptions> payments)
        {
            _waba = waba;
            _configuration = configuration;
            _payments = payments;
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
                    Status = string.IsNullOrWhiteSpace(_configuration["OpenCage:ApiKey"]) ? "atencao" : "ok",
                    Detalhe = string.IsNullOrWhiteSpace(_configuration["OpenCage:ApiKey"])
                        ? "Sem chave do geocodificador: buscar endereco no mapa nao funciona."
                        : "Geocodificador configurado."
                },
                new()
                {
                    Chave = "pagamento", Nome = "Gateway de pagamento", Descricao = "Processamento de pedidos",
                    Status = string.IsNullOrWhiteSpace(_payments.Value.ApiKey) ? "atencao" : "ok",
                    Detalhe = string.IsNullOrWhiteSpace(_payments.Value.ApiKey)
                        ? "Gateway sem chave configurada."
                        : $"Ambiente {_payments.Value.Environment}. O simulador nao cobra: o pagamento e so um status."
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
