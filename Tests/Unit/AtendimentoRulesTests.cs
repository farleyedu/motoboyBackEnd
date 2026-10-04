using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class AtendimentoConfigRulesTests
    {
        [Fact]
        public void Default_mode_is_human_and_trimmed()
        {
            var result = AtendimentoConfigRules.Validate(new UpdateAtendimentoConfigRequest { SaudacaoHumano = "  Ola!  " });

            Assert.Equal("humano", result.Modo);
            Assert.Equal("Ola!", result.SaudacaoHumano);
            Assert.Null(result.MensagemForaHorario);
        }

        [Fact]
        public void Ia_mode_is_refused_until_the_module_exists()
        {
            var error = Assert.Throws<DeliveryDomainException>(() =>
                AtendimentoConfigRules.Validate(new UpdateAtendimentoConfigRequest { Modo = "IA" }));

            Assert.Equal("IA_NOT_AVAILABLE", error.Code);
            Assert.Equal("ia", AtendimentoConfigRules.Validate(new UpdateAtendimentoConfigRequest { Modo = "ia" }, iaAvailable: true).Modo);
        }

        [Fact]
        public void Unknown_mode_and_null_body_are_refused()
        {
            Assert.Throws<DeliveryDomainException>(() => AtendimentoConfigRules.Validate(new UpdateAtendimentoConfigRequest { Modo = "robo" }));
            Assert.Throws<DeliveryDomainException>(() => AtendimentoConfigRules.Validate(null));
        }

        private static HorarioAtendimentoDto Schedule(params (int Dia, string Abre, string Fecha)[] days) =>
            new() { Dias = days.Select(d => new HorarioDiaDto { Dia = d.Dia, Abre = d.Abre, Fecha = d.Fecha }).ToList() };

        [Fact]
        public void No_schedule_means_always_open_but_all_days_closed_means_closed()
        {
            Assert.True(AtendimentoConfigRules.IsOpen(null, new DateTime(2026, 9, 26, 3, 0, 0)));
            Assert.False(AtendimentoConfigRules.IsOpen(new HorarioAtendimentoDto(), new DateTime(2026, 9, 26, 3, 0, 0)));
        }

        [Fact]
        public void Open_only_inside_the_configured_window()
        {
            // 26/09/2026 e sabado (dia 6).
            var horario = Schedule((6, "11:00", "23:00"));

            Assert.True(AtendimentoConfigRules.IsOpen(horario, new DateTime(2026, 9, 26, 12, 0, 0)));
            Assert.False(AtendimentoConfigRules.IsOpen(horario, new DateTime(2026, 9, 26, 10, 59, 0)));
            Assert.False(AtendimentoConfigRules.IsOpen(horario, new DateTime(2026, 9, 26, 23, 0, 0)));
            Assert.False(AtendimentoConfigRules.IsOpen(horario, new DateTime(2026, 9, 27, 12, 0, 0))); // domingo sem horario
        }

        [Fact]
        public void Window_that_crosses_midnight_belongs_to_the_opening_day()
        {
            var horario = Schedule((5, "18:00", "02:00")); // sexta 18h ate sabado 02h

            Assert.True(AtendimentoConfigRules.IsOpen(horario, new DateTime(2026, 9, 25, 23, 0, 0))); // sexta 23h
            Assert.True(AtendimentoConfigRules.IsOpen(horario, new DateTime(2026, 9, 26, 1, 30, 0))); // sabado 01:30
            Assert.False(AtendimentoConfigRules.IsOpen(horario, new DateTime(2026, 9, 26, 2, 0, 0)));
            Assert.False(AtendimentoConfigRules.IsOpen(horario, new DateTime(2026, 9, 25, 17, 0, 0)));
        }
    }

    public class QuickReplyTests
    {
        private static IReadOnlyDictionary<string, string?> Values(params (string, string?)[] pairs) =>
            pairs.ToDictionary(p => p.Item1, p => p.Item2);

        [Fact]
        public void Variables_are_replaced_case_insensitively()
        {
            var result = QuickReplyRenderer.Render("Pedido {numero} de {Cliente}: chega as {previsao}. Total {total}.",
                Values(("numero", "1042"), ("cliente", "Maria"), ("previsao", "19:40"), ("total", "R$ 48,00")));

            Assert.Equal("Pedido 1042 de Maria: chega as 19:40. Total R$ 48,00.", result.Texto);
            Assert.Empty(result.Pendentes);
        }

        [Fact]
        public void Missing_value_is_kept_and_reported_never_blanked()
        {
            var result = QuickReplyRenderer.Render("O motoboy {motoboy} saiu com o pedido {numero}.", Values(("numero", "7"), ("motoboy", null)));

            Assert.Equal("O motoboy {motoboy} saiu com o pedido 7.", result.Texto);
            Assert.Equal(new[] { "motoboy" }, result.Pendentes);
        }

        [Fact]
        public void Text_without_variables_is_unchanged()
        {
            var result = QuickReplyRenderer.Render("Um momento, ja te respondo.", Values());

            Assert.Equal("Um momento, ja te respondo.", result.Texto);
        }

        [Fact]
        public void Normalize_trims_and_validates_shortcut_and_variables()
        {
            var ok = QuickReplyRules.Normalize(new SalvarRespostaRapidaRequest { Titulo = " Saiu ", Atalho = "/Saiu-1", Texto = " Pedido {numero} saiu. " });

            Assert.Equal("Saiu", ok.Titulo);
            Assert.Equal("saiu-1", ok.Atalho);
            Assert.Equal("Pedido {numero} saiu.", ok.Texto);
        }

        [Theory]
        [InlineData("", "ok", null)]
        [InlineData("t", "", null)]
        [InlineData("t", "ok", "com espaco")]
        [InlineData("t", "Ola {desconhecida}", null)]
        public void Normalize_refuses_bad_input(string titulo, string texto, string? atalho)
        {
            Assert.Throws<DeliveryDomainException>(() =>
                QuickReplyRules.Normalize(new SalvarRespostaRapidaRequest { Titulo = titulo, Texto = texto, Atalho = atalho }));
        }

        [Fact]
        public void Normalize_refuses_too_long_text()
        {
            Assert.Throws<DeliveryDomainException>(() =>
                QuickReplyRules.Normalize(new SalvarRespostaRapidaRequest { Titulo = "t", Texto = new string('a', QuickReplyRules.MaxTexto + 1) }));
        }
    }

    public class PhoneKeyTests
    {
        [Theory]
        [InlineData("+5534991230001", "34991230001")]
        [InlineData("(34) 99123-0001", "34991230001")]
        [InlineData("34991230001", "34991230001")]
        public void Different_formats_share_the_same_key(string phone, string expectedTail)
        {
            var key = PhoneKey.From(phone);

            Assert.NotNull(key);
            Assert.EndsWith(expectedTail[^8..], key!);
        }

        [Fact]
        public void Formats_of_the_same_number_are_equal()
        {
            Assert.Equal(PhoneKey.From("+5534991230001"), PhoneKey.From("(34) 99123-0001"));
        }

        [Fact]
        public void Too_short_or_empty_has_no_key()
        {
            Assert.Null(PhoneKey.From(null));
            Assert.Null(PhoneKey.From("123"));
            Assert.Null(PhoneKey.From("abc"));
        }

        [Theory]
        [InlineData("(34) 99123-0001", "+5534991230001")]
        [InlineData("34991230001", "+5534991230001")]
        [InlineData("+5534991230001", "+5534991230001")]
        [InlineData("5534991230001", "+5534991230001")]
        [InlineData("3432123456", "+553432123456")]
        [InlineData("12345", null)]
        [InlineData("", null)]
        public void To_e164(string phone, string? expected)
        {
            Assert.Equal(expected, PhoneKey.ToE164(phone));
        }
    }

    public class MotoboyMessageRulesTests
    {
        [Fact]
        public void Shortcut_fills_the_default_text_and_can_be_edited()
        {
            var (body, key) = MotoboyMessageRules.Normalize(null, "cliente_nao_atende", operatorSide: true);
            Assert.Contains("cliente", body, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("cliente_nao_atende", key);

            var (edited, _) = MotoboyMessageRules.Normalize("  Ligue para o cliente  ", "cliente_nao_atende", operatorSide: true);
            Assert.Equal("Ligue para o cliente", edited);
        }

        [Fact]
        public void Free_text_has_no_shortcut_and_is_bounded()
        {
            var (body, key) = MotoboyMessageRules.Normalize(" Oi ", null, operatorSide: false);

            Assert.Equal("Oi", body);
            Assert.Null(key);
            Assert.Throws<DeliveryDomainException>(() => MotoboyMessageRules.Normalize("", null, true));
            Assert.Throws<DeliveryDomainException>(() => MotoboyMessageRules.Normalize(new string('a', MotoboyMessageRules.MaxBody + 1), null, true));
        }

        [Fact]
        public void Shortcuts_are_per_side()
        {
            Assert.Throws<DeliveryDomainException>(() => MotoboyMessageRules.Normalize(null, "portao_trancado", operatorSide: true));
            var (body, _) = MotoboyMessageRules.Normalize(null, "portao_trancado", operatorSide: false);
            Assert.Contains("Portao", body);
            Assert.Throws<DeliveryDomainException>(() => MotoboyMessageRules.Normalize(null, "inexistente", operatorSide: false));
        }
    }

    public class PedidoFiltroConversaTests
    {
        [Fact]
        public void Conversation_id_is_parsed_or_refused()
        {
            var id = Guid.NewGuid();

            Assert.Equal(id, PedidoFiltro.From(new PedidoFiltroRequest { ConversaId = id.ToString() }).ConversaId);
            Assert.Null(PedidoFiltro.From(new PedidoFiltroRequest()).ConversaId);
            Assert.Throws<DeliveryDomainException>(() => PedidoFiltro.From(new PedidoFiltroRequest { ConversaId = "nao-e-guid" }));
        }
    }

    public class AtendimentoMigrationTests
    {
        private static string Read(string name)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "APIBack.csproj"))) dir = Path.GetDirectoryName(dir);
            return File.ReadAllText(Path.Combine(dir ?? string.Empty, "Migrations", "Delivery", name));
        }

        [Fact]
        public void Migration_is_additive_and_default_mode_is_human()
        {
            var sql = Read("20260927_03_atendimento.sql");

            Assert.Contains("modo TEXT NOT NULL DEFAULT 'humano'", sql);
            Assert.Contains("CHECK (modo IN ('humano', 'ia'))", sql);
            Assert.Contains("CREATE TABLE IF NOT EXISTS delivery_motoboy_message", sql);
            Assert.Contains("delivery_tracking_schema_versions", sql);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER TABLE conversas", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER TABLE mensagens", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Migration_runs_before_the_demo_seed()
        {
            Assert.True(string.CompareOrdinal("20260927_03_atendimento.sql", "20260927_90_seed_uberlandia.sql") < 0);
        }
    }

    public class PedidosAbertosRulesTests
    {
        private static HorarioAtendimentoDto Horario(params (int dia, string abre, string fecha)[] dias) =>
            new() { Dias = dias.Select(d => new HorarioDiaDto { Dia = d.dia, Abre = d.abre, Fecha = d.fecha }).ToList() };

        // 07/10/2026 e quarta-feira.
        private static DateTime Quarta(string hora) => DateTime.Parse("2026-10-07 " + hora);

        [Fact]
        public void Sem_horario_so_vale_o_interruptor()
        {
            Assert.True(PedidosAbertosRules.Avaliar(true, null, Quarta("03:00")).Aberto);
            var pausado = PedidosAbertosRules.Avaliar(false, null, Quarta("12:00"));
            Assert.False(pausado.Aberto);
            Assert.Equal(PedidosAbertosRules.Pausado, pausado.Motivo);
        }

        [Fact]
        public void Fora_do_horario_diz_quando_abre_hoje_amanha_ou_no_dia_da_semana()
        {
            var horario = Horario((3, "18:00", "23:00"), (5, "11:00", "15:00"));

            Assert.Equal("hoje às 18:00", PedidosAbertosRules.Avaliar(true, horario, Quarta("12:00")).AbreEm);
            Assert.Equal("sexta às 11:00", PedidosAbertosRules.Avaliar(true, horario, Quarta("23:30")).AbreEm);
            Assert.Equal("amanhã às 11:00", PedidosAbertosRules.Avaliar(true, Horario((4, "11:00", "15:00")), Quarta("23:30")).AbreEm);
        }

        [Fact]
        public void Dentro_do_horario_inclusive_na_virada_da_noite_esta_aberto()
        {
            Assert.True(PedidosAbertosRules.Avaliar(true, Horario((3, "18:00", "23:00")), Quarta("20:00")).Aberto);
            // quarta 18:00 ate quinta 02:00: quinta 01:00 ainda e a quarta.
            Assert.True(PedidosAbertosRules.Avaliar(true, Horario((3, "18:00", "02:00")), DateTime.Parse("2026-10-08 01:00")).Aberto);
            Assert.False(PedidosAbertosRules.Avaliar(true, Horario((3, "18:00", "02:00")), DateTime.Parse("2026-10-08 03:00")).Aberto);
        }

        [Fact]
        public void A_mensagem_para_o_cliente_explica_o_motivo()
        {
            Assert.Contains("pausou", PedidosAbertosRules.Mensagem(new SituacaoPedidos(false, PedidosAbertosRules.Pausado, null)));
            Assert.Contains("Abrimos amanhã às 11:00", PedidosAbertosRules.Mensagem(new SituacaoPedidos(false, PedidosAbertosRules.ForaDoHorario, "amanhã às 11:00")));
            Assert.Equal(string.Empty, PedidosAbertosRules.Mensagem(SituacaoPedidos.Aberta));
        }
    }
}
