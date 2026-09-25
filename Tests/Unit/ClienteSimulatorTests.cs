using System;
using System.Text.Json;
using APIBack.Automation.Infra;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class ClienteSimulatorTests
    {
        [Fact]
        public void WaId_KeepsOnlyDigits() =>
            Assert.Equal("5534999991234", SimulatedWebhook.WaId("+5534999991234"));

        [Fact]
        public void Payload_HasTheShapeTheWebhookControllerReads()
        {
            var body = SimulatedWebhook.BuildTextPayload(
                "831371026718601", "15551379162", "5534999991234", "Maria", "Oi, quero um pedido", "wamid.sim.1",
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("whatsapp_business_account", root.GetProperty("object").GetString());
            var change = root.GetProperty("entry")[0].GetProperty("changes")[0];
            Assert.Equal("messages", change.GetProperty("field").GetString());
            var value = change.GetProperty("value");
            Assert.Equal("whatsapp", value.GetProperty("messaging_product").GetString());
            Assert.Equal("831371026718601", value.GetProperty("metadata").GetProperty("phone_number_id").GetString());
            Assert.Equal("15551379162", value.GetProperty("metadata").GetProperty("display_phone_number").GetString());
            Assert.Equal("5534999991234", value.GetProperty("contacts")[0].GetProperty("wa_id").GetString());
            var message = value.GetProperty("messages")[0];
            Assert.Equal("5534999991234", message.GetProperty("from").GetString());
            Assert.Equal("wamid.sim.1", message.GetProperty("id").GetString());
            Assert.Equal("1700000000", message.GetProperty("timestamp").GetString());
            Assert.Equal("text", message.GetProperty("type").GetString());
            Assert.Equal("Oi, quero um pedido", message.GetProperty("text").GetProperty("body").GetString());
        }

        [Fact]
        public void Payload_KeepsAccentsAndQuotesIntact()
        {
            var body = SimulatedWebhook.BuildTextPayload("1", null, "5534999991234", "José", "Cadê o pedido? \"urgente\"", "wamid.sim.2", DateTimeOffset.UtcNow);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("Cadê o pedido? \"urgente\"",
                doc.RootElement.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value")
                    .GetProperty("messages")[0].GetProperty("text").GetProperty("body").GetString());
        }

        [Fact]
        public void Signature_IsAcceptedByTheRealValidator()
        {
            // Mesmo validador que o webhook usa em producao: a mensagem simulada tem que passar por ele.
            var options = Microsoft.Extensions.Options.Options.Create(new AutomationOptions
            {
                StrictSignatureValidation = true,
                Meta = new MetaOptions { AppSecret = "segredo-de-teste" }
            });
            var validator = new WebhookSignatureValidator(options);
            var body = SimulatedWebhook.BuildTextPayload("1", "2", "5534999991234", "Maria", "Oi", "wamid.sim.3", DateTimeOffset.UtcNow);

            Assert.True(validator.ValidarXHubSignature256(SimulatedWebhook.Sign("segredo-de-teste", body), body));
            Assert.False(validator.ValidarXHubSignature256(SimulatedWebhook.Sign("outro-segredo", body), body));
            Assert.False(validator.ValidarXHubSignature256(SimulatedWebhook.Sign("segredo-de-teste", body + " "), body));
        }

        [Fact]
        public void Migration_AddsSimuladoFlagWithSafeDefault()
        {
            var sql = System.IO.File.ReadAllText(System.IO.Path.Combine(
                AppContext.BaseDirectory, "Migrations", "Delivery", "20260929_02_clientes_cadastro.sql"));
            Assert.Contains("ADD COLUMN IF NOT EXISTS simulado BOOLEAN NOT NULL DEFAULT FALSE", sql, StringComparison.Ordinal);
        }
    }
}
