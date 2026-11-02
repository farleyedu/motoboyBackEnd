using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Atendimento.Motor;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;
using APIBack.IdentidadeWhatsapp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>
    /// A3: o destino da mensagem e capturado no ingresso e nunca recalculado. Troca A -> B com fila pendente, destinos simultaneos,
    /// reenvio, eco, WABA errada, acesso revogado, contato ambiguo e o bot do restaurante intacto.
    /// </summary>
    public class WhatsappRoteamentoSeguroTests
    {
        private const string Phone1 = "821317791056700";
        private const string Phone2 = "821317791056799";
        private const string Cliente = "5534999990000";
        private static readonly Guid LojaA = Guid.NewGuid();
        private static readonly Guid LojaB = Guid.NewGuid();
        private static readonly Guid LojaGestao = Guid.NewGuid();

        /// <summary>wa_evento em memoria: ON CONFLICT (tipo, chave) DO NOTHING, igual ao banco.</summary>
        private sealed class FilaEmMemoria : IWaEventoRepository
        {
            public List<WaEvento> Eventos { get; } = new();
            public Dictionary<Guid, (string Estado, int? Codigo, string? Motivo)> Estados { get; } = new();
            public Dictionary<Guid, string> Categorias { get; } = new();

            public Task<bool> RegistrarAsync(WaEvento evento)
            {
                if (Eventos.Any(e => e.Tipo == evento.Tipo && e.Chave == evento.Chave)) return Task.FromResult(false);
                Eventos.Add(evento);
                return Task.FromResult(true);
            }

            public Task<IReadOnlyList<WaEvento>> ReivindicarAsync(int max) => Task.FromResult<IReadOnlyList<WaEvento>>(Eventos.Take(max).ToList());
            public Task MarcarProcessadoAsync(Guid id) { Estados[id] = ("processado", null, null); return Task.CompletedTask; }
            public Task MarcarIgnoradoAsync(Guid id, string motivo) { Estados[id] = ("ignorado", null, motivo); return Task.CompletedTask; }
            public Task MarcarBloqueadoAsync(Guid id, int codigo, string motivo) { Estados[id] = ("bloqueado", codigo, motivo); return Task.CompletedTask; }
            public Task RegistrarCategoriaAsync(Guid id, string categoria) { Categorias[id] = categoria; return Task.CompletedTask; }
            public Task ReagendarAsync(Guid id, string erro, TimeSpan espera, bool definitivo) { Estados[id] = (definitivo ? "erro" : "pendente", null, erro); return Task.CompletedTask; }
            public Task<int> RecuperarPresosAsync(TimeSpan alem) => Task.FromResult(0);
            public Task<ResumoFila> ResumoAsync() => Task.FromResult(new ResumoFila(0, 0, null));
        }

        private sealed class Fixture
        {
            public FilaEmMemoria Fila { get; } = new();
            public List<CanalWhatsapp> Banco { get; } = new();
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<IPipelineDeMensagem> Pipeline { get; } = new();
            public Mock<IIdentidadeWhatsappService> Identidade { get; } = new();
            public List<(ConversationProcessingInput Input, CanalWhatsapp Canal)> Entregues { get; } = new();
            public List<(ConversationProcessingInput Input, ContextoGestao Contexto)> EntreguesGestao { get; } = new();

            public Fixture()
            {
                Canais.Setup(c => c.ObterAtivoPorPhoneNumberIdAsync(It.IsAny<string>()))
                    .ReturnsAsync((string p) => Banco.FirstOrDefault(c => c.PhoneNumberId == p && c.Status != StatusCanal.Inativo));
                Canais.Setup(c => c.ObterAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) => Banco.FirstOrDefault(c => c.Id == id));
                Pipeline.Setup(p => p.ExecutarAsync(It.IsAny<ConversationProcessingInput>(), It.IsAny<CanalWhatsapp>()))
                    .Callback<ConversationProcessingInput, CanalWhatsapp>((i, c) => Entregues.Add((i, c))).Returns(Task.CompletedTask);
                Pipeline.Setup(p => p.ExecutarGestaoAsync(It.IsAny<ConversationProcessingInput>(), It.IsAny<CanalWhatsapp>(), It.IsAny<ContextoGestao>()))
                    .Callback<ConversationProcessingInput, CanalWhatsapp, ContextoGestao>((i, _, c) => EntreguesGestao.Add((i, c))).ReturnsAsync(true);
            }

            public CanalWhatsapp Teste(string phone, Guid loja, string finalidade = FinalidadeTeste.Geral)
            {
                var canal = new CanalWhatsapp
                {
                    Id = Guid.NewGuid(), PhoneNumberId = phone, IdEstabelecimento = loja, Status = StatusCanal.Ativo, Origem = OrigemCanal.Zippy,
                    Teste = true, FinalidadeTeste = finalidade, ModoAtendimento = ModoAtendimento.Hibrido
                };
                Banco.Add(canal);
                return canal;
            }

            public WaWebhookIngestor Ingestor() => new(Fila, NullLogger<WaWebhookIngestor>.Instance, Canais.Object);

            public WaEventoProcessor Processador(bool comIdentidade = true) =>
                new(Canais.Object, new Mock<IMessageService>().Object, Pipeline.Object, NullLogger<WaEventoProcessor>.Instance,
                    identidade: comIdentidade ? Identidade.Object : null);

            public async Task<WaEvento> ReceberAsync(string wamid, string phone = Phone1, string de = Cliente, string? waba = null, string corpoMensagem = "")
            {
                var corpo = corpoMensagem.Length > 0 ? corpoMensagem : $@"""type"":""text"",""text"":{{""body"":""quero o cardapio""}}";
                var json = $@"{{""entry"":[{{""id"":""{waba ?? "700000000000001"}"",""changes"":[{{""field"":""messages"",""value"":{{
                    ""metadata"":{{""display_phone_number"":""5534991480112"",""phone_number_id"":""{phone}""}},
                    ""contacts"":[{{""wa_id"":""{de}""}}],
                    ""messages"":[{{""id"":""{wamid}"",""from"":""{de}"",""timestamp"":""1790980926"",{corpo}}}]}}}}]}}]}}";
                using var doc = JsonDocument.Parse(json);
                await Ingestor().IngerirAsync(doc.RootElement, canal: null);
                return Fila.Eventos.Single(e => e.Chave == wamid);
            }

            public void Remetente(string destino, int? usuario = null, AutorizacaoWhatsapp? autorizacao = null)
            {
                Identidade.Setup(i => i.ResolverRemetenteAsync(It.IsAny<string?>(), false))
                    .ReturnsAsync(new RemetenteResolvido(destino, usuario, false, destino is DestinoRemetente.AtendimentoPublico or DestinoRemetente.ConfirmarPapel, false));
                Identidade.Setup(i => i.AutorizarAsync(It.IsAny<string?>())).ReturnsAsync(autorizacao);
            }
        }

        private static AutorizacaoWhatsapp Autorizacao(int usuario) => new(usuario, Guid.NewGuid(), 3, new[]
        {
            new EscopoAutorizado(Guid.NewGuid(), LojaGestao, "gerente",
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["crm"] = new() { "visualizar" } })
        });

        // ---- troca de destino -----------------------------------------------------------------------------------

        [Fact]
        public async Task Mensagem_enfileirada_em_A_e_reprocessada_depois_da_troca_para_B_nunca_entra_em_B()
        {
            var f = new Fixture();
            var canal = f.Teste(Phone1, LojaA);
            var pendente = await f.ReceberAsync("wamid.A1");

            // Operador move o numero para B com a mensagem ainda na fila.
            canal.IdEstabelecimento = LojaB;
            var primeira = await f.Processador().ProcessarAsync(pendente);
            var reprocessada = await f.Processador().ProcessarAsync(pendente);

            Assert.Equal(LojaA, pendente.IdEstabelecimentoDestino);
            Assert.Equal(canal.Id, pendente.IdCanalDestino);
            foreach (var r in new[] { primeira, reprocessada })
            {
                Assert.Equal("bloqueado", r.Resultado);
                Assert.Equal(409, r.Codigo);
                Assert.StartsWith(RotaBloqueada.DestinoAlterado, r.Motivo);
            }
            Assert.Empty(f.Entregues);
            f.Canais.Verify(c => c.ObterAtivoPorPhoneNumberIdAsync(It.IsAny<string>()), Times.Once); // so no ingresso, nunca no worker
            f.Canais.Verify(c => c.MarcarRecebimentoAsync(It.IsAny<Guid>()), Times.Never);

            // A mensagem nova, que chegou depois da troca, vai para B.
            var nova = await f.ReceberAsync("wamid.B1");
            var r2 = await f.Processador().ProcessarAsync(nova);
            Assert.Equal("processado", r2.Resultado);
            Assert.Equal(LojaB, Assert.Single(f.Entregues).Input.IdEstabelecimento);
        }

        [Fact]
        public async Task Mensagem_capturada_em_A_ainda_e_entregue_em_A_se_o_numero_nao_mudou()
        {
            var f = new Fixture();
            var canal = f.Teste(Phone1, LojaA);
            var evento = await f.ReceberAsync("wamid.A1");

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal("processado", r.Resultado);
            var (input, entregue) = Assert.Single(f.Entregues);
            Assert.Equal(LojaA, input.IdEstabelecimento);
            Assert.Equal(canal.Id, input.IdCanal);
            Assert.Same(canal, entregue);
        }

        [Fact]
        public async Task Numero_que_troca_de_finalidade_depois_do_ingresso_bloqueia_com_409()
        {
            var f = new Fixture();
            var canal = f.Teste(Phone1, LojaA);
            var evento = await f.ReceberAsync("wamid.A1");

            canal.FinalidadeTeste = FinalidadeTeste.Gestao;
            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(409, r.Codigo);
            Assert.StartsWith(RotaBloqueada.FinalidadeAlterada, r.Motivo);
            Assert.Empty(f.Entregues);
            Assert.Empty(f.EntreguesGestao);
        }

        [Fact]
        public async Task Numero_desconhecido_no_ingresso_nao_e_recalculado_mesmo_se_cadastrado_depois()
        {
            var f = new Fixture();
            var evento = await f.ReceberAsync("wamid.X");
            f.Teste(Phone1, LojaB); // cadastrado depois do ingresso

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(RotaEvento.SemCanal, evento.Rota);
            Assert.Equal("bloqueado", r.Resultado);
            Assert.Equal(422, r.Codigo);
            Assert.StartsWith(RotaBloqueada.CanalDesconhecido, r.Motivo);
            Assert.Empty(f.Entregues);
        }

        // ---- destinos simultaneos, reenvio e eco ---------------------------------------------------------------

        [Fact]
        public async Task Dois_numeros_de_teste_em_destinos_diferentes_nao_se_misturam()
        {
            var f = new Fixture();
            var paraA = f.Teste(Phone1, LojaA);
            var paraB = f.Teste(Phone2, LojaB);

            var e1 = await f.ReceberAsync("wamid.1", Phone1);
            var e2 = await f.ReceberAsync("wamid.2", Phone2);
            await f.Processador().ProcessarAsync(e2);
            await f.Processador().ProcessarAsync(e1);

            Assert.Equal(2, f.Entregues.Count);
            Assert.Contains(f.Entregues, x => x.Input.Mensagem.Id == "wamid.1" && x.Input.IdEstabelecimento == LojaA && x.Canal.Id == paraA.Id);
            Assert.Contains(f.Entregues, x => x.Input.Mensagem.Id == "wamid.2" && x.Input.IdEstabelecimento == LojaB && x.Canal.Id == paraB.Id);
        }

        [Fact]
        public async Task Reenvio_da_Meta_depois_da_troca_nao_duplica_nem_sobrescreve_o_destino_capturado()
        {
            var f = new Fixture();
            var canal = f.Teste(Phone1, LojaA);
            var original = await f.ReceberAsync("wamid.A1");

            canal.IdEstabelecimento = LojaB;
            var reenviado = await f.ReceberAsync("wamid.A1");

            Assert.Single(f.Fila.Eventos);
            Assert.Same(original, reenviado);
            Assert.Equal(LojaA, reenviado.IdEstabelecimentoDestino);
            Assert.Equal("bloqueado", (await f.Processador().ProcessarAsync(reenviado)).Resultado);
        }

        [Fact]
        public async Task Eco_do_proprio_numero_nunca_aciona_o_bot()
        {
            var ingresso = new Mock<IIngressoDeConversa>();
            var executor = new Mock<IExecutorDeAtendimento>();
            var pipeline = new PipelineDeMensagem(ingresso.Object, new Mock<IConversationRepository>().Object, executor.Object,
                new Mock<IEnviadorDeRespostas>().Object, NullLogger<PipelineDeMensagem>.Instance);
            var canal = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = LojaGestao, PhoneNumberId = Phone1 };
            var input = new ConversationProcessingInput(new WebhookMessageDto { Id = "wamid.E", De = "5534991480112", Tipo = "text" }, "oi",
                "5534991480112", Phone1, DateTime.UtcNow, new WebhookChangeValueDto(), "oi", LojaGestao, canal.Id);

            await pipeline.ExecutarAsync(input, canal);
            var gestao = await pipeline.ExecutarGestaoAsync(input, canal,
                new ContextoGestao(DestinoRemetente.Qualificacao, CategoriaRemetente.Desconhecido, null, new ReferenciaMidia(null, null, "text", "wamid.E")));

            Assert.True(gestao);
            ingresso.VerifyNoOtherCalls();
            executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Pipeline_recusa_mensagem_cuja_loja_capturada_difere_da_loja_do_canal()
        {
            var ingresso = new Mock<IIngressoDeConversa>();
            var pipeline = new PipelineDeMensagem(ingresso.Object, new Mock<IConversationRepository>().Object, new Mock<IExecutorDeAtendimento>().Object,
                new Mock<IEnviadorDeRespostas>().Object, NullLogger<PipelineDeMensagem>.Instance);
            var canal = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = LojaB, PhoneNumberId = Phone1 };
            var input = new ConversationProcessingInput(new WebhookMessageDto { Id = "wamid.A1", De = Cliente, Tipo = "text" }, "oi",
                "5534991480112", Phone1, DateTime.UtcNow, new WebhookChangeValueDto(), "oi", LojaA, canal.Id);

            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecutarAsync(input, canal));
            ingresso.VerifyNoOtherCalls();
        }

        // ---- seguranca da rota ----------------------------------------------------------------------------------

        [Fact]
        public async Task Waba_incorreta_bloqueia_com_403_e_nao_entrega()
        {
            var f = new Fixture();
            f.Banco.Add(new CanalWhatsapp
            {
                Id = Guid.NewGuid(), PhoneNumberId = Phone1, IdEstabelecimento = LojaA, Status = StatusCanal.Ativo,
                Origem = OrigemCanal.Autorizado, WabaId = "700000000000001"
            });
            var evento = await f.ReceberAsync("wamid.W", waba: "700000000000002");

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(403, r.Codigo);
            Assert.StartsWith(RotaBloqueada.WabaIncorreta, r.Motivo);
            Assert.Empty(f.Entregues);
        }

        [Fact]
        public async Task Numero_desativado_depois_do_ingresso_bloqueia_com_403()
        {
            var f = new Fixture();
            var canal = f.Teste(Phone1, LojaA);
            var evento = await f.ReceberAsync("wamid.R");

            canal.Status = StatusCanal.Inativo;
            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(403, r.Codigo);
            Assert.StartsWith(RotaBloqueada.AcessoRevogado, r.Motivo);
            Assert.Empty(f.Entregues);
        }

        [Fact]
        public async Task Worker_grava_bloqueado_com_codigo_e_nunca_marca_processado()
        {
            var evento = new WaEvento { Id = Guid.NewGuid(), Tipo = TipoEvento.Mensagem, Chave = "wamid.B", Tentativas = 1 };
            var fila = new Mock<IWaEventoRepository>();
            var entregue = false;
            fila.Setup(f => f.ReivindicarAsync(It.IsAny<int>())).ReturnsAsync(() =>
            {
                if (entregue) return Array.Empty<WaEvento>();
                entregue = true;
                return new[] { evento };
            });
            var processador = new Mock<IWaEventoProcessor>();
            processador.Setup(p => p.ProcessarAsync(evento)).ReturnsAsync(ResultadoEvento.Bloqueado(409, "destino_alterado: A -> B", CategoriaRemetente.Ambiguo));
            var services = new ServiceCollection();
            services.AddScoped(_ => fila.Object);
            services.AddScoped(_ => processador.Object);
            var worker = new WaEventoWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<WaEventoWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            for (var i = 0; i < 100 && !fila.Invocations.Any(x => x.Method.Name == "RegistrarCategoriaAsync"); i++) await Task.Delay(30);
            await worker.StopAsync(CancellationToken.None);

            fila.Verify(f => f.MarcarBloqueadoAsync(evento.Id, 409, "destino_alterado: A -> B"), Times.Once);
            fila.Verify(f => f.RegistrarCategoriaAsync(evento.Id, CategoriaRemetente.Ambiguo), Times.Once);
            fila.Verify(f => f.MarcarProcessadoAsync(It.IsAny<Guid>()), Times.Never);
        }

        // ---- loja Gestao: cada identidade no seu fluxo ----------------------------------------------------------

        [Fact]
        public async Task Restaurante_comum_segue_o_pipeline_de_sempre_sem_consultar_identidade()
        {
            var f = new Fixture();
            f.Teste(Phone1, LojaA);
            var evento = await f.ReceberAsync("wamid.A1");

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(RotaEvento.Operacional, evento.Rota);
            Assert.Equal("processado", r.Resultado);
            Assert.Null(r.Categoria);
            Assert.Single(f.Entregues);
            Assert.Empty(f.EntreguesGestao);
            f.Identidade.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Funcionario_verificado_na_loja_Gestao_vai_ao_assistente_interno_com_as_permissoes_dele()
        {
            var f = new Fixture();
            f.Teste(Phone1, LojaGestao, FinalidadeTeste.Gestao);
            var autorizacao = Autorizacao(7);
            f.Remetente(DestinoRemetente.AssistenteInterno, 7, autorizacao);
            var evento = await f.ReceberAsync("wamid.G1");

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(RotaEvento.Gestao, evento.Rota);
            Assert.Equal("processado", r.Resultado);
            Assert.Equal(CategoriaRemetente.FuncionarioVerificado, r.Categoria);
            var (input, contexto) = Assert.Single(f.EntreguesGestao);
            Assert.Same(autorizacao, contexto.Autorizacao);
            Assert.Equal(LojaGestao, input.IdEstabelecimento);
            Assert.Empty(f.Entregues);
        }

        [Fact]
        public async Task Assistente_interno_recebe_a_mensagem_e_nunca_passa_pelo_bot_publico()
        {
            var assistente = new Mock<IAssistenteInternoWhatsapp>();
            var ingresso = new Mock<IIngressoDeConversa>();
            var executor = new Mock<IExecutorDeAtendimento>();
            var pipeline = new PipelineDeMensagem(ingresso.Object, new Mock<IConversationRepository>().Object, executor.Object,
                new Mock<IEnviadorDeRespostas>().Object, NullLogger<PipelineDeMensagem>.Instance, assistente: assistente.Object);
            var canal = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = LojaGestao, PhoneNumberId = Phone1 };
            var input = new ConversationProcessingInput(new WebhookMessageDto { Id = "wamid.G1", De = Cliente, Tipo = "audio" }, "[audio]",
                "5534991480112", Phone1, DateTime.UtcNow, new WebhookChangeValueDto(), null, LojaGestao, canal.Id);
            var autorizacao = Autorizacao(7);

            var ok = await pipeline.ExecutarGestaoAsync(input, canal, new ContextoGestao(DestinoRemetente.AssistenteInterno,
                CategoriaRemetente.FuncionarioVerificado, autorizacao, new ReferenciaMidia("MID.1", "audio/ogg", "audio", "wamid.G1")));

            Assert.True(ok);
            assistente.Verify(a => a.AtenderAsync(It.Is<MensagemInterna>(m =>
                m.Autorizacao == autorizacao && m.LojaGestaoId == LojaGestao && m.Midia.MidiaId == "MID.1" && m.Midia.Audio), canal), Times.Once);
            ingresso.VerifyNoOtherCalls();
            executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Sem_assistente_interno_o_funcionario_fica_bloqueado_e_nao_cai_no_bot_publico()
        {
            var f = new Fixture();
            f.Teste(Phone1, LojaGestao, FinalidadeTeste.Gestao);
            f.Remetente(DestinoRemetente.AssistenteInterno, 7, Autorizacao(7));
            f.Pipeline.Setup(p => p.ExecutarGestaoAsync(It.IsAny<ConversationProcessingInput>(), It.IsAny<CanalWhatsapp>(), It.IsAny<ContextoGestao>()))
                .ReturnsAsync(false);
            var evento = await f.ReceberAsync("wamid.G1");

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal("bloqueado", r.Resultado);
            Assert.Equal(422, r.Codigo);
            Assert.Empty(f.Entregues);

            var pipeline = new PipelineDeMensagem(new Mock<IIngressoDeConversa>().Object, new Mock<IConversationRepository>().Object,
                new Mock<IExecutorDeAtendimento>().Object, new Mock<IEnviadorDeRespostas>().Object, NullLogger<PipelineDeMensagem>.Instance);
            var input = new ConversationProcessingInput(new WebhookMessageDto { Id = "wamid.G1", De = Cliente, Tipo = "text" }, "oi",
                "5534991480112", Phone1, DateTime.UtcNow, new WebhookChangeValueDto(), "oi", LojaGestao, Guid.NewGuid());
            Assert.False(await pipeline.ExecutarGestaoAsync(input, new CanalWhatsapp { IdEstabelecimento = LojaGestao, PhoneNumberId = Phone1 },
                new ContextoGestao(DestinoRemetente.AssistenteInterno, CategoriaRemetente.FuncionarioVerificado, Autorizacao(7), new ReferenciaMidia(null, null, "text", "wamid.G1"))));
        }

        [Fact]
        public async Task Autorizacao_que_cai_entre_a_classificacao_e_a_entrega_bloqueia_com_403()
        {
            var f = new Fixture();
            f.Teste(Phone1, LojaGestao, FinalidadeTeste.Gestao);
            f.Remetente(DestinoRemetente.AssistenteInterno, 7, autorizacao: null);
            var evento = await f.ReceberAsync("wamid.G1");

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(403, r.Codigo);
            Assert.StartsWith(RotaBloqueada.AutorizacaoRevogada, r.Motivo);
            Assert.Empty(f.EntreguesGestao);
            Assert.Empty(f.Entregues);
        }

        [Fact]
        public async Task Vinculo_revogado_resolve_como_desconhecido_e_vai_para_a_qualificacao_sem_nada_interno()
        {
            var f = new Fixture();
            f.Teste(Phone1, LojaGestao, FinalidadeTeste.Gestao);
            f.Remetente(DestinoRemetente.Qualificacao);
            var evento = await f.ReceberAsync("wamid.G2");

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(CategoriaRemetente.Desconhecido, r.Categoria);
            var (_, contexto) = Assert.Single(f.EntreguesGestao);
            Assert.Equal(DestinoRemetente.Qualificacao, contexto.Destino);
            Assert.Null(contexto.Autorizacao);
            f.Identidade.Verify(i => i.AutorizarAsync(It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task Contato_do_CRM_na_loja_Gestao_vai_ao_atendimento_publico()
        {
            var f = new Fixture();
            f.Teste(Phone1, LojaGestao, FinalidadeTeste.Gestao);
            f.Remetente(DestinoRemetente.AtendimentoPublico);
            var evento = await f.ReceberAsync("wamid.G3");

            var r = await f.Processador().ProcessarAsync(evento);

            Assert.Equal(CategoriaRemetente.ContatoCrm, r.Categoria);
            Assert.Null(Assert.Single(f.EntreguesGestao).Contexto.Autorizacao);
        }

        [Fact]
        public async Task Contato_ambiguo_recebe_pedido_de_identificacao_e_nem_bot_nem_assistente_respondem()
        {
            var conversa = Guid.NewGuid();
            var ingresso = new Mock<IIngressoDeConversa>();
            ingresso.Setup(i => i.AcrescentarEntradaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
                .ReturnsAsync(new ConversationIngressResult(new Message { IdConversa = conversa }, false, null, false, true));
            var executor = new Mock<IExecutorDeAtendimento>();
            var enviador = new Mock<IEnviadorDeRespostas>();
            var assistente = new Mock<IAssistenteInternoWhatsapp>();
            var pipeline = new PipelineDeMensagem(ingresso.Object, new Mock<IConversationRepository>().Object, executor.Object,
                enviador.Object, NullLogger<PipelineDeMensagem>.Instance, assistente: assistente.Object);
            var canal = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = LojaGestao, PhoneNumberId = Phone1 };
            var input = new ConversationProcessingInput(new WebhookMessageDto { Id = "wamid.AMB", De = Cliente, Tipo = "text" }, "oi",
                "5534991480112", Phone1, DateTime.UtcNow, new WebhookChangeValueDto(), "oi", LojaGestao, canal.Id);

            var ok = await pipeline.ExecutarGestaoAsync(input, canal, new ContextoGestao(DestinoRemetente.ConfirmarPapel,
                CategoriaRemetente.Ambiguo, null, new ReferenciaMidia(null, null, "text", "wamid.AMB")));

            Assert.True(ok);
            enviador.Verify(e => e.EnviarAsync(conversa, canal, "+5534999990000",
                It.Is<AcaoMotor>(a => a is AcaoResponder && ((AcaoResponder)a).Mensagem == PipelineDeMensagem.PedidoIdentificacao)), Times.Once);
            executor.VerifyNoOtherCalls();
            assistente.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Rota_gestao_sem_servico_de_identidade_bloqueia_em_vez_de_cair_no_fluxo_publico()
        {
            var f = new Fixture();
            f.Teste(Phone1, LojaGestao, FinalidadeTeste.Gestao);
            var evento = await f.ReceberAsync("wamid.G4");

            var r = await f.Processador(comIdentidade: false).ProcessarAsync(evento);

            Assert.Equal(422, r.Codigo);
            Assert.Empty(f.Entregues);
            Assert.Empty(f.EntreguesGestao);
        }

        // ---- midia ----------------------------------------------------------------------------------------------

        [Fact]
        public async Task Audio_e_guardado_so_como_referencia_e_segue_para_a_Gestao_sem_interpretar()
        {
            var f = new Fixture();
            f.Teste(Phone1, LojaGestao, FinalidadeTeste.Gestao);
            f.Remetente(DestinoRemetente.Qualificacao);
            var evento = await f.ReceberAsync("wamid.AUD",
                corpoMensagem: @"""type"":""audio"",""audio"":{""id"":""MID.123"",""mime_type"":""audio/ogg; codecs=opus"",""voice"":true}");

            await f.Processador().ProcessarAsync(evento);

            Assert.Equal("audio", evento.TipoMensagem);
            Assert.Equal("MID.123", evento.MidiaId);
            Assert.Equal("audio/ogg; codecs=opus", evento.MidiaMime);
            Assert.Equal(Cliente, evento.Remetente);
            Assert.NotNull(evento.MensagemEm);
            var midia = Assert.Single(f.EntreguesGestao).Contexto.Midia;
            Assert.True(midia.Audio);
            Assert.Equal("MID.123", midia.MidiaId);
        }
    }
}
