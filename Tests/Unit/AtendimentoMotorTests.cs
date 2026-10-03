using System;
using System.Linq;
using APIBack.Atendimento;
using APIBack.Atendimento.Motor;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class AtendimentoMotorTests
    {
        private static readonly DateTime Agora = new(2026, 10, 2, 23, 0, 0, DateTimeKind.Utc);
        private static readonly AtendimentoMotor Motor = new(new IFluxoDeServico[] { new FluxoCardapioWeb(), new FluxoDelivery(), new FluxoAgendamento() });

        private static EntradaMotor Entrada(
            string texto = "oi",
            string[]? servicos = null,
            string modo = ModoAtendimento.Hibrido,
            string? interpretado = null,
            bool primeira = true,
            bool foraDoHorario = false,
            string? url = "https://zippy.app/cardapio/97c5d396") => new(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Pizza Bom Centro", modo,
                servicos ?? new[] { "cardapio_web" },
                texto, interpretado ?? texto, primeira, foraDoHorario, null, null, url);

        private static string Texto(ResultadoMotor r) =>
            string.Join("\n", r.Acoes.OfType<AcaoResponder>().Select(a => a.Mensagem).Concat(r.Acoes.OfType<AcaoBotoes>().Select(b => b.Mensagem)));

        // ---- um servico so: entra direto ------------------------------------------------------------------------------

        [Fact]
        public void Numero_com_so_o_cardapio_web_manda_o_link_com_a_saudacao_na_primeira_mensagem()
        {
            var r = Motor.Decidir(Entrada(), null, Agora);

            var texto = Texto(r);
            Assert.Contains("Olá! Aqui é a Pizza Bom Centro.", texto);
            Assert.Contains("https://zippy.app/cardapio/97c5d396", texto);
            Assert.Equal("cardapio_web", r.Fluxo);
            Assert.StartsWith("servico_unico", r.Regra);
            Assert.DoesNotContain(r.Acoes, a => a is AcaoChamarAtendente);
        }

        [Fact]
        public void Na_segunda_mensagem_nao_repete_a_saudacao()
        {
            var r = Motor.Decidir(Entrada(primeira: false), null, Agora);

            Assert.DoesNotContain("Olá! Aqui é a", Texto(r));
            Assert.Contains("https://zippy.app", Texto(r));
        }

        [Fact]
        public void Sem_a_url_do_cardapio_configurada_passa_para_a_equipe_em_vez_de_ficar_mudo()
        {
            var r = Motor.Decidir(Entrada(url: null), null, Agora);

            Assert.Contains(r.Acoes, a => a is AcaoChamarAtendente c && c.Motivo.Contains("sem link"));
            Assert.DoesNotContain("http", Texto(r));
        }

        // ---- menu com varios servicos ---------------------------------------------------------------------------------

        [Fact]
        public void Dois_servicos_mostram_o_menu_com_botoes_e_a_opcao_de_atendente_no_modo_hibrido()
        {
            var r = Motor.Decidir(Entrada(servicos: new[] { "cardapio_web", "agendamento" }), null, Agora);

            var botoes = Assert.IsType<AcaoBotoes>(Assert.Single(r.Acoes));
            Assert.Equal(new[] { "svc_agendamento", "svc_cardapio_web", "menu_atendente" }, botoes.Opcoes.Select(o => o.Id).ToArray());
            Assert.Equal("menu", r.Passo);
        }

        [Fact]
        public void No_modo_bot_o_menu_nao_oferece_falar_com_atendente()
        {
            var r = Motor.Decidir(Entrada(servicos: new[] { "cardapio_web", "agendamento" }, modo: ModoAtendimento.Bot), null, Agora);

            var botoes = Assert.IsType<AcaoBotoes>(Assert.Single(r.Acoes));
            Assert.DoesNotContain(botoes.Opcoes, o => o.Id == "menu_atendente");
        }

        [Fact]
        public void Mais_de_tres_opcoes_viram_lista_numerada_e_o_numero_escolhe_o_servico()
        {
            var servicos = new[] { "cardapio_web", "agendamento", "delivery" };
            var menu = Motor.Decidir(Entrada(servicos: servicos), null, Agora);

            var texto = Texto(menu);
            Assert.Contains("1 - Agendar", texto);
            Assert.Contains("4 - Falar com atendente", texto);
            Assert.IsType<AcaoResponder>(Assert.Single(menu.Acoes));

            var escolha = Motor.Decidir(Entrada(texto: "2", interpretado: "2", servicos: servicos, primeira: false), menu.Estado, Agora);
            Assert.Equal("cardapio_web", escolha.Fluxo);
            Assert.Contains("https://zippy.app", Texto(escolha));

            var atendente = Motor.Decidir(Entrada(texto: "4", interpretado: "4", servicos: servicos, primeira: false), menu.Estado, Agora);
            Assert.Contains(atendente.Acoes, a => a is AcaoChamarAtendente);
        }

        [Fact]
        public void Tocar_no_botao_de_um_servico_entra_no_fluxo_dele()
        {
            var r = Motor.Decidir(
                Entrada(texto: "Fazer pedido", interpretado: "svc_cardapio_web", servicos: new[] { "cardapio_web", "agendamento" }, primeira: false), null, Agora);

            Assert.Equal("cardapio_web", r.Fluxo);
            Assert.Equal("menu:servico>link_do_cardapio", r.Regra);
        }

        [Fact]
        public void Botao_de_servico_que_o_numero_nao_atende_e_ignorado_e_cai_no_menu()
        {
            var r = Motor.Decidir(
                Entrada(texto: "x", interpretado: "svc_agendamento", servicos: new[] { "cardapio_web", "delivery" }, primeira: false), null, Agora);

            Assert.IsType<AcaoBotoes>(Assert.Single(r.Acoes));
            Assert.Equal("menu", r.Passo);
        }

        // ---- atendente --------------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("quero falar com um atendente")]
        [InlineData("ATENDENTE")]
        [InlineData("Humano!")]
        [InlineData("falar com alguém")]
        public void Pedir_atendente_no_modo_hibrido_avisa_o_cliente_e_chama_a_equipe(string texto)
        {
            var r = Motor.Decidir(Entrada(texto: texto, servicos: new[] { "cardapio_web" }, primeira: false), null, Agora);

            Assert.Contains(r.Acoes, a => a is AcaoChamarAtendente);
            Assert.Equal("comando:atendente", r.Regra);
            Assert.Null(r.Estado.Servico);
        }

        [Fact]
        public void No_modo_bot_pedir_atendente_nao_chama_a_equipe_e_volta_ao_menu()
        {
            var r = Motor.Decidir(Entrada(texto: "quero um atendente", servicos: new[] { "cardapio_web", "agendamento" }, modo: ModoAtendimento.Bot), null, Agora);

            Assert.DoesNotContain(r.Acoes, a => a is AcaoChamarAtendente);
            Assert.Equal("comando:atendente_indisponivel", r.Regra);
            Assert.Contains(r.Acoes, a => a is AcaoBotoes);
        }

        [Fact]
        public void Numero_sem_nenhum_servico_responde_e_chama_a_equipe()
        {
            var r = Motor.Decidir(Entrada(servicos: Array.Empty<string>()), null, Agora);

            Assert.Contains(r.Acoes, a => a is AcaoChamarAtendente c && c.Motivo.Contains("sem servico"));
            Assert.Equal("numero_sem_servico", r.Regra);
        }

        [Fact]
        public void Servico_desconhecido_do_catalogo_e_ignorado_pelo_motor()
        {
            var r = Motor.Decidir(Entrada(servicos: new[] { "venda_de_carros" }), null, Agora);

            Assert.Equal("numero_sem_servico", r.Regra);
        }

        // ---- atalhos e palavras -------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("menu")]
        [InlineData("Início")]
        [InlineData("voltar")]
        [InlineData("0")]
        public void Comandos_de_menu_voltam_ao_menu_e_limpam_o_fluxo(string comando)
        {
            var estado = new EstadoFluxo { Servico = "cardapio_web", Passo = "qualquer" };

            var r = Motor.Decidir(Entrada(texto: comando, servicos: new[] { "cardapio_web", "agendamento" }, primeira: false), estado, Agora);

            Assert.Equal("comando:menu", r.Regra);
            Assert.Null(r.Estado.Servico);
            Assert.Equal("menu", r.Estado.Passo);
        }

        [Theory]
        [InlineData("qual o cardápio?", "cardapio_web")]
        [InlineData("quero fazer um pedido", "cardapio_web")]
        [InlineData("QUERO AGENDAR UM HORÁRIO", "agendamento")]
        [InlineData("onde está meu pedido", "delivery")]
        public void Palavra_chave_leva_direto_ao_servico_do_numero(string texto, string esperado)
        {
            var r = Motor.Decidir(Entrada(texto: texto, interpretado: null, servicos: new[] { "cardapio_web", "agendamento", "delivery" }, primeira: false), null, Agora);

            Assert.Equal(esperado, r.Fluxo);
            Assert.StartsWith("gatilho:", r.Regra);
        }

        [Fact]
        public void Palavra_de_um_servico_que_o_numero_nao_atende_nao_dispara_o_fluxo()
        {
            var r = Motor.Decidir(Entrada(texto: "quero agendar", interpretado: null, servicos: new[] { "cardapio_web" }, primeira: false), null, Agora);

            Assert.Equal("cardapio_web", r.Fluxo); // numero de servico unico: cai no servico dele
            Assert.StartsWith("servico_unico", r.Regra);
        }

        [Fact]
        public void Agendamento_so_mostra_o_encaixe_e_chama_a_equipe()
        {
            var r = Motor.Decidir(Entrada(texto: "agendar", servicos: new[] { "agendamento" }, primeira: false), null, Agora);

            Assert.Contains("em breve", Texto(r));
            Assert.Contains(r.Acoes, a => a is AcaoChamarAtendente);
            Assert.Null(r.Estado.Servico);
        }

        // ---- fora do horario ----------------------------------------------------------------------------------------------

        [Fact]
        public void Fora_do_horario_avisa_uma_vez_e_continua_atendendo()
        {
            var primeira = Motor.Decidir(Entrada(foraDoHorario: true), null, Agora);

            Assert.Contains("fechado", Texto(primeira));
            Assert.Contains("https://zippy.app", Texto(primeira));
            Assert.Equal(Agora, primeira.Estado.ForaDoHorarioAvisadoEm);

            var logo_depois = Motor.Decidir(Entrada(foraDoHorario: true, primeira: false), primeira.Estado, Agora.AddMinutes(10));
            Assert.DoesNotContain("fechado", Texto(logo_depois));

            var seis_horas_depois = Motor.Decidir(Entrada(foraDoHorario: true, primeira: false), primeira.Estado, Agora.AddHours(7));
            Assert.Contains("fechado", Texto(seis_horas_depois));
        }

        [Fact]
        public void A_mensagem_de_fora_do_horario_da_loja_vale_mais_que_a_padrao()
        {
            var entrada = Entrada(foraDoHorario: true) with { MensagemForaDoHorario = "Voltamos amanhã às 11h!" };

            Assert.Contains("Voltamos amanhã às 11h!", Texto(Motor.Decidir(entrada, null, Agora)));
        }

        [Fact]
        public void A_saudacao_configurada_da_loja_substitui_a_padrao()
        {
            var entrada = Entrada() with { Saudacao = "Fala, chefe! Bem-vindo à Bom Centro." };

            var texto = Texto(Motor.Decidir(entrada, null, Agora));

            Assert.Contains("Fala, chefe!", texto);
            Assert.DoesNotContain("Olá! Aqui é a", texto);
        }

        // ---- robustez -----------------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("???")]
        [InlineData("asdkjh qweoiu")]
        public void Entrada_vazia_ou_sem_sentido_nunca_deixa_o_cliente_sem_resposta(string texto)
        {
            var r = Motor.Decidir(Entrada(texto: texto, interpretado: null, servicos: new[] { "cardapio_web", "agendamento" }, primeira: false), null, Agora);

            Assert.NotEmpty(r.Acoes);
        }

        [Fact]
        public void Estado_de_um_servico_que_deixou_de_existir_nao_derruba_o_motor()
        {
            var estado = new EstadoFluxo { Servico = "garagem", Passo = "veiculo" };

            var r = Motor.Decidir(Entrada(texto: "oi", servicos: new[] { "cardapio_web" }, primeira: false), estado, Agora);

            Assert.Equal("cardapio_web", r.Fluxo);
        }

        [Theory]
        [InlineData("Olá, Tudo BEM?", "ola, tudo bem")]
        [InlineData("  Início!!  ", "inicio")]
        [InlineData("AÇÃO  rápida", "acao rapida")]
        [InlineData(null, "")]
        public void Normalizar_tira_acento_caixa_e_pontuacao_das_pontas(string? entrada, string esperado) =>
            Assert.Equal(esperado, AtendimentoMotor.Normalizar(entrada));
    }
}
