using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class ProntidaoRegrasTests
    {
        private static readonly DateTimeOffset Agora = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        /// <summary>Loja com tudo certo, com os modulos de delivery, cardapio web e remuneracao.</summary>
        internal static ProntidaoDados Pronta(Action<ProntidaoDados>? ajustar = null)
        {
            var d = new ProntidaoDados
            {
                Situacao = SituacoesComerciais.Implantacao,
                TipoEstabelecimentoId = Guid.NewGuid(),
                PlanoComercial = "rota",
                Modulos = new[] { "PEDIDOS", "DELIVERY", "CARDAPIO", "CARDAPIOWEB", "WHATSAPP", "REMUNERACAO", "IFOOD", "IMPRESSAO" },
                ResponsaveisAtivos = 1, ResponsaveisComSenha = 1,
                Logradouro = "Rua A", Numero = "10", Bairro = "Centro", Cidade = "Uberlandia", Uf = "MG", Cep = "38400-000",
                Latitude = -18.91, Longitude = -48.27,
                DiasAbertos = 6, RaioEntregaKm = 8m, ProdutosVendaveis = 3, ProdutosTotal = 3, ProdutosComImagem = 2, Categorias = 2,
                CardapioWebConfigurado = true, CardapioWebPublicado = true, NomePublico = "Pizza Bom",
                Canais = new[] { new CanalEstadoLeitura("ativo", "zippy", null, new DateTime(2026, 10, 24), null) },
                TaxaEntregaFixa = 5m, TempoPreparoMin = 30, PedidoMinimo = 20m, UrlLogo = "http://x/logo.png", CorPrimaria = "#fff",
                Telefone = "3433330000", Email = "a@b.com", Cnpj = "12345678000199",
                IfoodAutorizado = true, ImpressaoOnline = true, Atendentes = 1, MotoboysVinculados = 2, MotoboysComPlano = 0,
                RemuneracaoPadraoDefinida = true,
                InfraDeliveryTracking = true, InfraMapsApiKey = true, InfraChaveToken = true, InfraCardapioBaseUrl = true,
                InfraMetaAppSecret = true, InfraMetaVerifyToken = true
            };
            ajustar?.Invoke(d);
            return d;
        }

        private static ProntidaoItemDto Item(ProntidaoDto p, string codigo) => p.Itens.Single(i => i.Codigo == codigo);

        [Fact]
        public void Loja_completa_pode_comecar_a_operar_com_100_por_cento()
        {
            var p = ProntidaoRegras.Avaliar(Pronta(), false, Agora);

            Assert.True(p.PodeComecarAOperar);
            Assert.Equal(0, p.PendenciasBloqueantes);
            Assert.Equal(100, p.Percentual);
            Assert.All(p.Itens.Where(i => i.Bloqueia), i => Assert.Equal("ok", i.Status));
        }

        [Theory]
        [InlineData(ProntidaoRegras.TipoEstabelecimento)]
        [InlineData(ProntidaoRegras.PlanoModulos)]
        [InlineData(ProntidaoRegras.GerenteSenha)]
        [InlineData(ProntidaoRegras.Endereco)]
        [InlineData(ProntidaoRegras.Coordenada)]
        [InlineData(ProntidaoRegras.HorarioSemanal)]
        [InlineData(ProntidaoRegras.RaioEntrega)]
        [InlineData(ProntidaoRegras.ProdutoVendavel)]
        [InlineData(ProntidaoRegras.CardapioWeb)]
        [InlineData(ProntidaoRegras.WhatsappRecebendo)]
        public void Cada_item_obrigatorio_pendente_impede_o_inicio_e_diz_como_resolver(string codigo)
        {
            var p = ProntidaoRegras.Avaliar(Pronta(Estragar(codigo)), false, Agora);

            var item = Item(p, codigo);
            Assert.Equal("pendente", item.Status);
            Assert.True(item.Bloqueia);
            Assert.False(string.IsNullOrWhiteSpace(item.ComoResolver));
            Assert.False(p.PodeComecarAOperar);
            Assert.Equal(1, p.PendenciasBloqueantes);
            Assert.True(p.Percentual < 100);
        }

        private static Action<ProntidaoDados> Estragar(string codigo) => codigo switch
        {
            ProntidaoRegras.TipoEstabelecimento => d => d.TipoEstabelecimentoId = null,
            ProntidaoRegras.PlanoModulos => d => d.PlanoComercial = null,
            ProntidaoRegras.GerenteSenha => d => d.ResponsaveisComSenha = 0,
            ProntidaoRegras.Endereco => d => d.Cep = "123",
            ProntidaoRegras.Coordenada => d => { d.Latitude = 0; d.Longitude = 0; },
            ProntidaoRegras.HorarioSemanal => d => d.DiasAbertos = 0,
            ProntidaoRegras.RaioEntrega => d => d.RaioEntregaKm = 0m,
            ProntidaoRegras.ProdutoVendavel => d => d.ProdutosVendaveis = 0,
            ProntidaoRegras.CardapioWeb => d => d.CardapioWebPublicado = false,
            ProntidaoRegras.WhatsappRecebendo => d => d.Canais = Array.Empty<CanalEstadoLeitura>(),
            _ => throw new ArgumentOutOfRangeException(nameof(codigo))
        };

        [Theory]
        [InlineData(null, 0.0)]
        [InlineData(0.0, 0.0)]
        public void Coordenada_nula_ou_zero_zero_nao_vale(double? latitude, double? longitude)
        {
            var d = Pronta(x => { x.Latitude = latitude; x.Longitude = latitude == null ? null : longitude; });

            Assert.False(ProntidaoRegras.CoordenadaValida(d.Latitude, d.Longitude));
            Assert.True(ProntidaoRegras.CoordenadaValida(-18.9, -48.2));
            Assert.False(ProntidaoRegras.CoordenadaValida(95, 10));
            Assert.False(ProntidaoRegras.CoordenadaValida(double.NaN, 10));
        }

        [Theory]
        [InlineData("MG", "38400-000", true)]
        [InlineData("M", "38400-000", false)]
        [InlineData("MG", "3840", false)]
        [InlineData("M1", "38400000", false)]
        public void Endereco_exige_uf_de_duas_letras_e_cep_de_oito_digitos(string uf, string cep, bool completo) =>
            Assert.Equal(completo, ProntidaoRegras.EnderecoCompleto(Pronta(d => { d.Uf = uf; d.Cep = cep; })));

        [Fact]
        public void Raio_de_entrega_nulo_ou_zero_e_pendencia()
        {
            Assert.Equal("pendente", Item(ProntidaoRegras.Avaliar(Pronta(d => d.RaioEntregaKm = null), false, Agora), ProntidaoRegras.RaioEntrega).Status);
            Assert.Equal("pendente", Item(ProntidaoRegras.Avaliar(Pronta(d => d.RaioEntregaKm = 0m), false, Agora), ProntidaoRegras.RaioEntrega).Status);
            Assert.Equal("ok", Item(ProntidaoRegras.Avaliar(Pronta(d => d.RaioEntregaKm = 0.5m), false, Agora), ProntidaoRegras.RaioEntrega).Status);
        }

        [Fact]
        public void Basta_um_produto_para_vender()
        {
            var p = ProntidaoRegras.Avaliar(Pronta(d => d.ProdutosVendaveis = 1), false, Agora);

            Assert.Equal("ok", Item(p, ProntidaoRegras.ProdutoVendavel).Status);
        }

        [Fact]
        public void Sem_o_modulo_de_cardapio_web_ele_nao_se_aplica_e_o_whatsapp_vira_so_aviso()
        {
            var p = ProntidaoRegras.Avaliar(Pronta(d =>
            {
                d.Modulos = new[] { "PEDIDOS", "DELIVERY" };
                d.CardapioWebConfigurado = false;
                d.Canais = Array.Empty<CanalEstadoLeitura>();
            }), false, Agora);

            Assert.Equal("nao_se_aplica", Item(p, ProntidaoRegras.CardapioWeb).Status);
            Assert.False(Item(p, ProntidaoRegras.CardapioWeb).Bloqueia);
            var whatsapp = Item(p, ProntidaoRegras.WhatsappRecebendo);
            Assert.Equal("aviso", whatsapp.Status);
            Assert.False(whatsapp.Bloqueia);
            Assert.True(p.PodeComecarAOperar);
        }

        [Fact]
        public void Itens_recomendados_nunca_bloqueiam()
        {
            var p = ProntidaoRegras.Avaliar(Pronta(d =>
            {
                d.TaxaEntregaFixa = 0; d.TaxaEntregaPorKm = 0; d.ZonasAtivas = 0; d.TempoPreparoMin = null; d.PedidoMinimo = null;
                d.Categorias = 0; d.UrlLogo = null; d.Telefone = null; d.Cnpj = null; d.IfoodAutorizado = false; d.ImpressaoOnline = false;
                d.Atendentes = 0; d.MotoboysVinculados = 0; d.RemuneracaoPadraoDefinida = false;
            }), false, Agora);

            var avisos = p.Itens.Where(i => !i.Bloqueia && i.Status == "aviso").Select(i => i.Codigo).ToList();
            foreach (var esperado in new[] { "taxa_entrega", "tempo_preparo", "pedido_minimo", "categorias_imagens", "marca", "contato", "cnpj", "ifood", "impressao", "equipe", "motoboys", "remuneracao_padrao" })
                Assert.Contains(esperado, avisos);
            Assert.True(p.PodeComecarAOperar);
            Assert.Equal(100, p.Percentual);
        }

        [Fact]
        public void Remuneracao_so_se_aplica_com_delivery_e_remuneracao_e_aceita_o_padrao_ou_o_plano_de_todos()
        {
            Assert.Equal("nao_se_aplica", Item(ProntidaoRegras.Avaliar(Pronta(d => d.Modulos = new[] { "DELIVERY" }), false, Agora), "remuneracao_padrao").Status);
            Assert.Equal("nao_se_aplica", Item(ProntidaoRegras.Avaliar(Pronta(d => d.Modulos = new[] { "REMUNERACAO" }), false, Agora), "remuneracao_padrao").Status);
            Assert.Equal("ok", Item(ProntidaoRegras.Avaliar(Pronta(d => { d.RemuneracaoPadraoDefinida = false; d.MotoboysVinculados = 2; d.MotoboysComPlano = 2; }), false, Agora), "remuneracao_padrao").Status);
            Assert.Equal("aviso", Item(ProntidaoRegras.Avaliar(Pronta(d => { d.RemuneracaoPadraoDefinida = false; d.MotoboysVinculados = 2; d.MotoboysComPlano = 1; }), false, Agora), "remuneracao_padrao").Status);
            Assert.Equal("ok", Item(ProntidaoRegras.Avaliar(Pronta(d => { d.RemuneracaoPadraoDefinida = true; d.MotoboysVinculados = 0; }), false, Agora), "remuneracao_padrao").Status);
        }

        [Fact]
        public void Loja_ativa_antiga_sem_plano_comercial_nao_trava_no_plano()
        {
            var p = ProntidaoRegras.Avaliar(Pronta(d => { d.Situacao = SituacoesComerciais.Ativo; d.PlanoComercial = null; }), false, Agora);

            Assert.Equal("ok", Item(p, ProntidaoRegras.PlanoModulos).Status);
            Assert.Equal("pendente", Item(ProntidaoRegras.Avaliar(Pronta(d => d.PlanoComercial = null), false, Agora), ProntidaoRegras.PlanoModulos).Status);
            Assert.Equal("pendente", Item(ProntidaoRegras.Avaliar(Pronta(d => d.Modulos = Array.Empty<string>()), false, Agora), ProntidaoRegras.PlanoModulos).Status);
        }

        // ---- estado do WhatsApp (leitura) -------------------------------------------------------------------------------

        [Fact]
        public void Whatsapp_recebendo_depende_de_um_numero_ativo_que_recebeu_mensagem()
        {
            var recebeu = new DateTime(2026, 10, 24);
            Assert.True(ProntidaoRegras.WhatsappEstaRecebendo(new[] { new CanalEstadoLeitura("ativo", "zippy", null, recebeu, null) }));
            Assert.True(ProntidaoRegras.WhatsappEstaRecebendo(new[] { new CanalEstadoLeitura("configurando", "zippy", null, recebeu, null) }));
            // Sem nenhum recebimento ainda.
            Assert.False(ProntidaoRegras.WhatsappEstaRecebendo(new[] { new CanalEstadoLeitura("ativo", "zippy", null, null, null) }));
            Assert.False(ProntidaoRegras.WhatsappEstaRecebendo(Array.Empty<CanalEstadoLeitura>()));
        }

        [Theory]
        [InlineData("inativo")]
        [InlineData("erro")]
        public void Numero_inativo_ou_com_erro_nao_conta_mesmo_que_ja_tenha_recebido(string status) =>
            Assert.False(ProntidaoRegras.WhatsappEstaRecebendo(new[] { new CanalEstadoLeitura(status, "zippy", "recebendo", new DateTime(2026, 10, 24), null) }));

        [Fact]
        public void Numero_proprio_vale_pelo_estado_do_webhook_e_o_da_zippy_ou_central_por_envio_ok()
        {
            Assert.True(ProntidaoRegras.WhatsappEstaRecebendo(new[] { new CanalEstadoLeitura("ativo", "proprio", "recebendo", null, null) }));
            Assert.False(ProntidaoRegras.WhatsappEstaRecebendo(new[] { new CanalEstadoLeitura("ativo", "proprio", "registrado", null, new DateTime(2026, 10, 24)) }));
            Assert.True(ProntidaoRegras.WhatsappEstaRecebendo(new[] { new CanalEstadoLeitura("ativo", "central", "nao_aplicavel", null, new DateTime(2026, 10, 24)) }));
            Assert.True(ProntidaoRegras.WhatsappEstaRecebendo(new[]
            {
                new CanalEstadoLeitura("inativo", "zippy", null, new DateTime(2026, 10, 1), null),
                new CanalEstadoLeitura("ativo", "zippy", null, new DateTime(2026, 10, 24), null)
            }));
        }

        // ---- infraestrutura -------------------------------------------------------------------------------------------

        [Fact]
        public void Infra_o_cliente_so_ve_aguardando_a_zippy_e_a_gestao_ve_o_detalhe()
        {
            var dados = Pronta(d => d.InfraMapsApiKey = false);

            var loja = ProntidaoRegras.Avaliar(dados, false, Agora);
            var infraLoja = loja.Itens.Where(i => i.Grupo == "infra").ToList();
            var unico = Assert.Single(infraLoja);
            Assert.Equal("Aguardando a Zippy", unico.Titulo);
            Assert.Equal("pendente", unico.Status);
            Assert.True(unico.Bloqueia);
            Assert.Equal(QuemResolve.Infra, unico.QuemResolve);
            Assert.DoesNotContain("Google", unico.Mensagem + unico.ComoResolver);
            Assert.False(loja.PodeComecarAOperar);

            var gestao = ProntidaoRegras.Avaliar(dados, true, Agora);
            var google = gestao.Itens.Single(i => i.Codigo == "infra_google_maps");
            Assert.Equal("pendente", google.Status);
            Assert.Contains("Google__MapsApiKey", google.ComoResolver);
            Assert.True(gestao.Itens.Count(i => i.Grupo == "infra") >= 6);
            Assert.False(gestao.PodeComecarAOperar);
        }

        [Fact]
        public void Infra_do_whatsapp_so_conta_para_lojas_com_whatsapp_ou_cardapio_web()
        {
            var semWhatsapp = Pronta(d =>
            {
                d.Modulos = new[] { "PEDIDOS", "DELIVERY" };
                d.InfraChaveToken = false; d.InfraCardapioBaseUrl = false; d.InfraMetaAppSecret = false; d.InfraMetaVerifyToken = false;
            });
            Assert.True(ProntidaoRegras.Avaliar(semWhatsapp, false, Agora).PodeComecarAOperar);

            var comWhatsapp = Pronta(d => d.InfraMetaAppSecret = false);
            Assert.False(ProntidaoRegras.Avaliar(comWhatsapp, false, Agora).PodeComecarAOperar);
        }

        // ---- teste de 15 dias -------------------------------------------------------------------------------------------

        [Fact]
        public void Dias_do_teste_restantes_sao_negativos_quando_vencido_e_so_existem_no_teste()
        {
            var fim = Agora.AddDays(10);
            Assert.Equal(10, ProntidaoRegras.DiasRestantes(fim, Agora));
            Assert.Equal(1, ProntidaoRegras.DiasRestantes(Agora.AddHours(3), Agora));
            Assert.Equal(-1, ProntidaoRegras.DiasRestantes(Agora.AddHours(-3), Agora));
            Assert.Equal(-3, ProntidaoRegras.DiasRestantes(Agora.AddDays(-3), Agora));
            Assert.Null(ProntidaoRegras.DiasRestantes(null, Agora));

            var teste = ProntidaoRegras.Avaliar(Pronta(d => { d.Situacao = SituacoesComerciais.Teste; d.TesteTerminaEm = fim; }), false, Agora);
            Assert.Equal(fim, teste.TesteTerminaEm);
            Assert.Equal(10, teste.DiasTesteRestantes);

            var ativo = ProntidaoRegras.Avaliar(Pronta(d => { d.Situacao = SituacoesComerciais.Ativo; d.TesteTerminaEm = fim; }), false, Agora);
            Assert.Null(ativo.TesteTerminaEm);
            Assert.Null(ativo.DiasTesteRestantes);

            var vencido = ProntidaoRegras.Avaliar(Pronta(d => { d.Situacao = SituacoesComerciais.Teste; d.TesteTerminaEm = Agora.AddDays(-2); }), false, Agora);
            Assert.True(vencido.DiasTesteRestantes < 0);
        }

        [Fact]
        public void O_teste_dura_15_dias_a_partir_do_inicio()
        {
            Assert.Equal(15, SituacaoComercialRules.DiasTeste);
            Assert.Equal(Agora.AddDays(15), SituacaoComercialRules.FimDoTeste(Agora));
        }

        // ---- resumo e pendencias --------------------------------------------------------------------------------------------

        [Fact]
        public void Pendencias_listam_so_o_que_bloqueia_e_o_resumo_traz_as_tres_principais()
        {
            var p = ProntidaoRegras.Avaliar(Pronta(d => { d.DiasAbertos = 0; d.RaioEntregaKm = null; d.ProdutosVendaveis = 0; d.Cep = null; d.Cnpj = null; }), false, Agora);

            var pendencias = ProntidaoRegras.Pendencias(p);
            Assert.Equal(4, pendencias.Count);
            Assert.All(pendencias, x => Assert.False(string.IsNullOrWhiteSpace(x.ComoResolver)));
            Assert.Equal(2, ProntidaoRegras.Pendencias(p, 2).Count);
            var resumo = ProntidaoRegras.Resumir(p);
            Assert.Equal(3, resumo.Principais.Count);
            Assert.False(resumo.PodeComecarAOperar);
            Assert.Equal(4, resumo.PendenciasBloqueantes);
        }

        [Fact]
        public void Todo_item_aponta_para_onde_corrigir_com_as_chaves_das_configuracoes()
        {
            var validas = new HashSet<string> { "negocio", "endereco", "horarios", "delivery", "atendimento", "numeros", "ifood", "impressao", "equipe", "remuneracao" };
            var p = ProntidaoRegras.Avaliar(Pronta(d =>
            {
                d.DiasAbertos = 0; d.Cep = null; d.TaxaEntregaFixa = 0; d.Atendentes = 0; d.IfoodAutorizado = false; d.ImpressaoOnline = false;
                d.RemuneracaoPadraoDefinida = false; d.MotoboysVinculados = 0; d.Canais = Array.Empty<CanalEstadoLeitura>();
            }), false, Agora);

            foreach (var item in p.Itens.Where(i => i.Status is "pendente" or "aviso" && i.Grupo != "infra"))
            {
                var c = item.Correcao;
                Assert.True(c.Area != null || c.Rota != null || item.QuemResolve == QuemResolve.Zippy, $"{item.Codigo} sem caminho de correcao");
                if (c.Area != null) Assert.Contains(c.Area, validas);
                if (c.Foco != null) Assert.Contains(c.Foco, validas);
                if (c.Rota != null) Assert.StartsWith("/apps/", c.Rota);
            }
        }

        [Fact]
        public void Severidades_ficam_numa_classe_so()
        {
            Assert.Contains(ProntidaoRegras.RaioEntrega, ProntidaoRegras.Bloqueantes);
            Assert.Contains(ProntidaoRegras.Coordenada, ProntidaoRegras.Bloqueantes);
            Assert.DoesNotContain("taxa_entrega", ProntidaoRegras.Bloqueantes);
        }
    }
}
