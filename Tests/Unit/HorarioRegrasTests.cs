using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.DTOs.Atendimento;
using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class HorarioRegrasTests
    {
        // 07/10/2026 e quarta-feira.
        private static DateTime Quarta(string hora) => DateTime.Parse("2026-10-07 " + hora);
        private static TimeSpan T(string hora) => TimeSpan.Parse(hora);

        private static HorarioSemana Semana(params (DayOfWeek Dia, string Abre, string Fecha)[] dias) => new()
        {
            TemSemanal = true,
            Semanal = dias.ToDictionary(d => d.Dia, d => new HorarioDia(false, T(d.Abre), T(d.Fecha)))
        };

        private static HorarioSemana ComEspecial(HorarioSemana baseSemana, DateOnly data, HorarioDia dia) => new()
        {
            TemSemanal = baseSemana.TemSemanal,
            Semanal = baseSemana.Semanal,
            Especiais = new Dictionary<DateOnly, HorarioDia>(baseSemana.Especiais) { [data] = dia }
        };

        [Fact]
        public void Sem_horario_semanal_e_sem_excecao_a_loja_esta_fechada_por_sem_horario()
        {
            var nulo = HorarioRegras.Avaliar(null, Quarta("12:00"));
            var vazio = HorarioRegras.Avaliar(new HorarioSemana(), Quarta("12:00"));

            Assert.False(nulo.Dentro);
            Assert.Equal(HorarioRegras.SemHorario, nulo.Motivo);
            Assert.False(vazio.Dentro);
            Assert.Equal(HorarioRegras.SemHorario, vazio.Motivo);
            Assert.Null(vazio.ProximaAbertura);
        }

        [Fact]
        public void Fora_do_horario_a_loja_esta_fechada_e_diz_quando_abre()
        {
            var semana = Semana((DayOfWeek.Wednesday, "18:00", "23:00"), (DayOfWeek.Friday, "11:00", "15:00"));

            var antes = HorarioRegras.Avaliar(semana, Quarta("12:00"));
            Assert.False(antes.Dentro);
            Assert.Equal(HorarioRegras.ForaHorario, antes.Motivo);
            Assert.Equal("hoje às 18:00", antes.ProximaAberturaTexto);
            Assert.Equal("sexta às 11:00", HorarioRegras.Avaliar(semana, Quarta("23:30")).ProximaAberturaTexto);
            Assert.Equal("amanhã às 11:00", HorarioRegras.Avaliar(Semana((DayOfWeek.Thursday, "11:00", "15:00")), Quarta("23:30")).ProximaAberturaTexto);
        }

        [Fact]
        public void Dentro_do_horario_a_loja_esta_aberta_e_o_fechamento_e_exclusivo()
        {
            var semana = Semana((DayOfWeek.Wednesday, "18:00", "23:00"));

            Assert.True(HorarioRegras.Avaliar(semana, Quarta("18:00")).Dentro);
            Assert.True(HorarioRegras.Avaliar(semana, Quarta("22:59")).Dentro);
            Assert.False(HorarioRegras.Avaliar(semana, Quarta("23:00")).Dentro);
            Assert.False(HorarioRegras.Avaliar(semana, Quarta("17:59")).Dentro);
        }

        [Fact]
        public void Turno_que_vira_a_meia_noite_continua_no_dia_seguinte()
        {
            var semana = Semana((DayOfWeek.Wednesday, "18:00", "02:00"));

            Assert.True(HorarioRegras.Avaliar(semana, Quarta("23:30")).Dentro);
            Assert.True(HorarioRegras.Avaliar(semana, DateTime.Parse("2026-10-08 01:59")).Dentro);
            Assert.False(HorarioRegras.Avaliar(semana, DateTime.Parse("2026-10-08 02:00")).Dentro);
            Assert.False(HorarioRegras.Avaliar(semana, DateTime.Parse("2026-10-08 12:00")).Dentro);
            // A madrugada de quarta pertence ao turno de terca, que nao existe.
            Assert.False(HorarioRegras.Avaliar(semana, Quarta("01:00")).Dentro);
        }

        [Fact]
        public void Excecao_do_dia_vale_no_lugar_da_semanal_e_nao_vaza_para_a_semana_seguinte()
        {
            var semana = ComEspecial(Semana((DayOfWeek.Wednesday, "18:00", "23:00")), new DateOnly(2026, 10, 7), new HorarioDia(false, T("09:00"), T("12:00")));

            Assert.True(HorarioRegras.Avaliar(semana, Quarta("10:00")).Dentro);
            Assert.False(HorarioRegras.Avaliar(semana, Quarta("19:00")).Dentro);
            Assert.True(HorarioRegras.Avaliar(semana, DateTime.Parse("2026-10-14 19:00")).Dentro);
        }

        [Fact]
        public void Excecao_fechada_fecha_o_dia_inteiro_e_excecao_sozinha_abre_loja_sem_grade()
        {
            var fechado = ComEspecial(Semana((DayOfWeek.Wednesday, "08:00", "23:00")), new DateOnly(2026, 10, 7), new HorarioDia(true, null, null));
            Assert.False(HorarioRegras.Avaliar(fechado, Quarta("12:00")).Dentro);

            var semGrade = ComEspecial(new HorarioSemana(), new DateOnly(2026, 10, 7), new HorarioDia(false, T("09:00"), T("18:00")));
            Assert.True(HorarioRegras.Avaliar(semGrade, Quarta("12:00")).Dentro);
            // Fora da excecao e sem grade: segue fechada, mas ja nao e SEM_HORARIO se hoje tem excecao.
            var depois = HorarioRegras.Avaliar(semGrade, Quarta("19:00"));
            Assert.False(depois.Dentro);
            Assert.Equal(HorarioRegras.ForaHorario, depois.Motivo);
            Assert.Equal(HorarioRegras.SemHorario, HorarioRegras.Avaliar(semGrade, DateTime.Parse("2026-10-08 12:00")).Motivo);
        }

        [Fact]
        public void Todos_os_dias_fechados_e_fora_do_horario_nao_sem_horario()
        {
            var semana = new HorarioSemana { TemSemanal = true, Semanal = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => new HorarioDia(true, null, null)) };

            var r = HorarioRegras.Avaliar(semana, Quarta("12:00"));

            Assert.False(r.Dentro);
            Assert.Equal(HorarioRegras.ForaHorario, r.Motivo);
            Assert.Null(r.ProximaAbertura);
        }

        // ---- fuso da loja ----------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("America/Sao_Paulo", 15)]
        [InlineData("America/Manaus", 14)]
        public void O_horario_local_usa_o_fuso_da_loja(string fuso, int horaLocal)
        {
            var utc = new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);

            Assert.Equal(horaLocal, HorarioRegras.ParaHorarioLocal(utc, fuso).Hour);
        }

        [Fact]
        public void A_mesma_hora_utc_abre_ou_fecha_a_loja_conforme_o_fuso()
        {
            var semana = Semana((DayOfWeek.Wednesday, "14:30", "15:30"));
            var utc = new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);

            // 18:00 UTC = 15:00 em Sao Paulo (aberta) e 14:00 em Manaus (ainda fechada).
            Assert.True(HorarioRegras.Avaliar(semana, HorarioRegras.ParaHorarioLocal(utc, "America/Sao_Paulo")).Dentro);
            Assert.False(HorarioRegras.Avaliar(semana, HorarioRegras.ParaHorarioLocal(utc, "America/Manaus")).Dentro);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Lugar/Nenhum")]
        public void Fuso_vazio_ou_invalido_cai_em_sao_paulo(string? fuso)
        {
            var utc = new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);

            Assert.Equal(15, HorarioRegras.ParaHorarioLocal(utc, fuso).Hour);
            Assert.Equal(15, AtendimentoConfigRules.ParaHorarioLocal(utc).Hour);
        }

        // ---- fechar agora ------------------------------------------------------------------------------------------------

        [Fact]
        public void Fechar_no_meio_do_turno_encerra_o_turno_de_hoje_neste_minuto()
        {
            var semana = Semana((DayOfWeek.Wednesday, "11:00", "23:00"));

            var plano = HorarioRegras.PlanejarFechamento(semana, Quarta("15:20"))!;

            Assert.Equal(new DateOnly(2026, 10, 7), plano.Data);
            Assert.False(plano.Fechado);
            Assert.Equal(T("11:00"), plano.Abre);
            Assert.Equal(T("15:20"), plano.Fecha);
            var depois = ComEspecial(semana, plano.Data, new HorarioDia(false, plano.Abre, plano.Fecha));
            Assert.False(HorarioRegras.Avaliar(depois, Quarta("15:21")).Dentro);
            Assert.True(HorarioRegras.Avaliar(depois, Quarta("12:00")).Dentro);
        }

        [Fact]
        public void Fechar_no_minuto_exato_da_abertura_fecha_o_dia_todo()
        {
            var plano = HorarioRegras.PlanejarFechamento(Semana((DayOfWeek.Wednesday, "11:00", "23:00")), Quarta("11:00"))!;

            Assert.True(plano.Fechado);
        }

        [Fact]
        public void Fechar_na_madrugada_de_um_turno_que_virou_a_noite_encerra_o_turno_de_ontem()
        {
            var semana = Semana((DayOfWeek.Tuesday, "18:00", "03:00"));
            var madrugada = DateTime.Parse("2026-10-07 01:10");

            var plano = HorarioRegras.PlanejarFechamento(semana, madrugada)!;

            Assert.Equal(new DateOnly(2026, 10, 6), plano.Data);
            Assert.Equal(T("18:00"), plano.Abre);
            Assert.Equal(T("01:10"), plano.Fecha);
            var depois = ComEspecial(semana, plano.Data, new HorarioDia(false, plano.Abre, plano.Fecha));
            Assert.False(HorarioRegras.Avaliar(depois, DateTime.Parse("2026-10-07 01:30")).Dentro);
            Assert.True(HorarioRegras.Avaliar(depois, DateTime.Parse("2026-10-06 22:00")).Dentro);
        }

        [Fact]
        public void Fechar_com_a_loja_ja_fechada_nao_planeja_nada()
        {
            Assert.Null(HorarioRegras.PlanejarFechamento(Semana((DayOfWeek.Wednesday, "18:00", "23:00")), Quarta("12:00")));
            Assert.Null(HorarioRegras.PlanejarFechamento(null, Quarta("12:00")));
        }

        // ---- a mesma regra para o cardapio web, o bot e o nucleo ---------------------------------------------------------

        [Fact]
        public void O_horario_do_atendimento_usa_a_regra_unica()
        {
            var sem = PedidosAbertosRules.Avaliar(true, null, Quarta("12:00"));
            Assert.False(sem.Aberto);
            Assert.Equal(PedidosAbertosRules.SemHorario, sem.Motivo);

            var horario = new HorarioAtendimentoDto { Dias = new List<HorarioDiaDto> { new() { Dia = 3, Abre = "18:00", Fecha = "23:00" } } };
            var fora = PedidosAbertosRules.Avaliar(true, horario, Quarta("12:00"));
            Assert.False(fora.Aberto);
            Assert.Equal(PedidosAbertosRules.ForaDoHorario, fora.Motivo);
            Assert.Equal("hoje às 18:00", fora.AbreEm);
            Assert.True(PedidosAbertosRules.Avaliar(true, horario, Quarta("20:00")).Aberto);
        }

        [Theory]
        [InlineData(11, 23, 12, true)]
        [InlineData(11, 23, 23, false)]
        [InlineData(18, 3, 2, false)]
        public void A_formula_estatica_do_repositorio_delega_para_a_regra_unica(int abre, int fecha, int agora, bool esperado) =>
            Assert.Equal(esperado, HorarioOperacaoRepository.HorarioAbertoEm(
                TimeSpan.FromHours(abre), TimeSpan.FromHours(fecha), false, null, null, true, TimeSpan.FromHours(agora)));
    }
}
