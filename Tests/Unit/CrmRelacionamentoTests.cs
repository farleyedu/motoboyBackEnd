using APIBack.Acompanhamento.Relacionamento;
using APIBack.DTOs.CRM;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>
/// Relacionamento do CRM: lead com proximo passo obrigatorio, parecidos que exigem decisao humana, idempotencia, passagem de loja
/// (concorrencia, aviso, visao de 7 dias), cobranca ao responsavel, marcacao, retencao/saida e a regra de nao inventar envio.
/// O store e um fake em memoria com trava por loja (o equivalente do FOR UPDATE).
/// </summary>
public class CrmRelacionamentoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero); // 12:00 em Brasilia
    private static readonly DateOnly Hoje = new(2026, 11, 2);

    private sealed class RelogioFalso : TimeProvider
    {
        public DateTimeOffset Agora { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    // ---------------------------------------------------------------- fake

    private sealed class Item
    {
        public Guid Id = Guid.NewGuid();
        public string Nome = "Loja Teste";
        public long Versao = 1;
        public int? Responsavel;
        public int? Anterior;
        public DateTimeOffset? Ate;
        public MarcacaoDto? Marcacao;
        public ProximoPassoDto? Passo;
        public DateTimeOffset? Enviada, Recebida;
        public bool CobrancaAberta;
        public readonly SemaphoreSlim Trava = new(1, 1);
        public readonly List<(string Tipo, int? Usuario, string Detalhe)> Eventos = new();
        public readonly List<(int Cobrador, DateTimeOffset Em)> Cobrancas = new();
        public readonly Dictionary<string, RetencaoDto> Retencoes = new();
    }

    private sealed class Store : IRelacionamentoStore
    {
        public readonly Dictionary<int, UsuarioEquipeDto> Equipe = new()
        {
            [1] = new(1, "Farley"), [2] = new(2, "Socio"), [3] = new(3, "Ana")
        };
        public readonly Dictionary<Guid, Item> Itens = new();
        public readonly List<(int Destinatario, string Tipo, string Mensagem)> Avisos = new();
        public readonly RelogioFalso Relogio;

        public Store(RelogioFalso relogio) => Relogio = relogio;

        public Item Novo(int? responsavel = null, string nome = "Loja Teste")
        {
            var i = new Item { Responsavel = responsavel, Nome = nome };
            Itens[i.Id] = i;
            return i;
        }

        public Task<Guid?> ResolverAsync(Guid id, CancellationToken ct) => Task.FromResult<Guid?>(Itens.ContainsKey(id) ? id : null);
        public Task<UsuarioEquipeDto?> UsuarioDaEquipeAsync(int usuarioId, CancellationToken ct) => Task.FromResult(Equipe.GetValueOrDefault(usuarioId));
        public Task<IReadOnlyList<UsuarioEquipeDto>> MembrosAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<UsuarioEquipeDto>>(Equipe.Values.ToList());

        public Task<IReadOnlyList<LojaCarteiraRow>> CarteiraAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LojaCarteiraRow>>(Itens.Values.Select(i => new LojaCarteiraRow
            {
                AcompanhamentoId = i.Id, Nome = i.Nome, Fase = "operando", ResponsavelUsuarioId = i.Responsavel, Versao = i.Versao,
                AnteriorUsuarioId = i.Anterior, VisivelAnteriorAte = i.Ate, ProximoPassoId = i.Passo?.Id, ProximoPassoTipo = i.Passo?.Tipo,
                ProximoPassoData = i.Passo?.Data, UltimaEnviada = i.Enviada, UltimaRecebida = i.Recebida, Marcacao = i.Marcacao?.Tipo
            }).ToList());

        public Task<IReadOnlyList<AvisoRelacionamentoDto>> AvisosAsync(int usuarioId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AvisoRelacionamentoDto>>(Avisos.Where(a => a.Destinatario == usuarioId)
                .Select((a, n) => new AvisoRelacionamentoDto(n, Guid.Empty, a.Tipo, a.Mensagem, null, T0, false)).ToList());

        public Task<int> MarcarAvisosLidosAsync(int usuarioId, IReadOnlyCollection<long> ids, CancellationToken ct) => Task.FromResult(ids.Count);

        public Task<RelacionamentoDto?> LerAsync(Guid id, int eventos, CancellationToken ct)
        {
            if (!Itens.TryGetValue(id, out var i)) return Task.FromResult<RelacionamentoDto?>(null);
            return Task.FromResult<RelacionamentoDto?>(new RelacionamentoDto
            {
                AcompanhamentoId = i.Id, Nome = i.Nome, Versao = i.Versao, Marcacao = i.Marcacao, ProximoPasso = i.Passo,
                Responsavel = i.Responsavel.HasValue ? Equipe[i.Responsavel.Value] : null,
                Espera = RelacionamentoRegras.CalcularEspera(i.Enviada, i.Recebida),
                VisaoAnterior = i.Anterior.HasValue && i.Ate > Relogio.Agora ? new VisaoAnteriorDto(i.Anterior.Value, i.Ate!.Value) : null,
                Eventos = i.Eventos.Select((e, n) => new EventoRelacionamentoDto(n, e.Tipo, e.Usuario, T0, null)).ToList()
            });
        }

        public async Task<IRelacionamentoTx> AbrirAsync(Guid id, CancellationToken ct)
        {
            var i = Itens[id];
            await i.Trava.WaitAsync(ct);
            return new Tx(this, i);
        }

        public Task<bool> RegistrarMarcoAsync(Guid id, string direcao, DateTimeOffset em, string referencia, CancellationToken ct)
        {
            var i = Itens[id];
            if (direcao == DirecoesContato.Enviada) i.Enviada = em; else i.Recebida = em;
            return Task.FromResult(true);
        }

        private sealed class Tx : IRelacionamentoTx
        {
            private readonly Store _s;
            private readonly Item _i;
            private bool _alterou, _livre;

            public Tx(Store s, Item i)
            {
                _s = s;
                _i = i;
                Estado = new RelacionamentoEstado { AcompanhamentoId = i.Id, Nome = i.Nome, Versao = i.Versao, ResponsavelUsuarioId = i.Responsavel, Marcacao = i.Marcacao, ProximoPasso = i.Passo };
            }

            public RelacionamentoEstado Estado { get; }

            public Task DefinirResponsavelAsync(int novo, int? anterior, DateTimeOffset ate, CancellationToken ct)
            {
                _i.Responsavel = novo; _i.Anterior = anterior; _i.Ate = anterior.HasValue ? ate : null; _alterou = true;
                return Task.CompletedTask;
            }

            public Task SubstituirProximoPassoAsync(string tipo, DateOnly data, string? nota, int usuarioId, CancellationToken ct)
            {
                _i.Passo = new ProximoPassoDto(Guid.NewGuid(), tipo, data, nota, usuarioId, _s.Relogio.Agora, false); _alterou = true;
                return Task.CompletedTask;
            }

            public Task AplicarMarcacaoAsync(string tipo, string motivo, int usuarioId, CancellationToken ct)
            {
                _i.Marcacao = new MarcacaoDto(tipo, motivo, usuarioId, _s.Relogio.Agora); _alterou = true;
                return Task.CompletedTask;
            }

            public Task RemoverMarcacaoAsync(int usuarioId, string motivo, CancellationToken ct) { _i.Marcacao = null; _alterou = true; return Task.CompletedTask; }
            public Task RegistrarCobrancaAsync(int cobrador, int responsavel, string? mensagem, CancellationToken ct) { _i.Cobrancas.Add((cobrador, _s.Relogio.Agora)); _alterou = true; return Task.CompletedTask; }

            public Task<DateTimeOffset?> UltimaCobrancaAsync(int cobrador, CancellationToken ct) =>
                Task.FromResult<DateTimeOffset?>(_i.Cobrancas.Where(c => c.Cobrador == cobrador).Select(c => (DateTimeOffset?)c.Em).DefaultIfEmpty(null).Max());

            public Task<RetencaoDto?> RetencaoPorChaveAsync(string chave, CancellationToken ct) => Task.FromResult(_i.Retencoes.GetValueOrDefault(chave));

            public Task<RetencaoDto> RegistrarRetencaoAsync(RetencaoRegistro r, CancellationToken ct)
            {
                var dto = new RetencaoDto
                {
                    Id = Guid.NewGuid(), AcompanhamentoId = _i.Id, Pedido = r.Pedido, PedidoEm = r.PedidoEm, Leitura = r.Leitura,
                    Oferta = new OfertaRetencaoDto { Tipo = r.OfertaTipo, Aprovada = r.OfertaAprovada, AprovadaPor = r.OfertaAprovadaPor, Valor = r.OfertaValor },
                    Canal = r.Canal, Resultado = r.Resultado, SaidaQuando = r.SaidaQuando, SaidaEm = r.SaidaEm, PausaAte = r.PausaAte,
                    CobrancaEmAberto = r.CobrancaEmAberto, CriadoPor = r.CriadoPor, CriadoEm = _s.Relogio.Agora
                };
                _i.Retencoes[r.Chave] = dto; _alterou = true;
                return Task.FromResult(dto);
            }

            public Task<bool> CobrancaEmAbertoAsync(CancellationToken ct) => Task.FromResult(_i.CobrancaAberta);
            public Task AvisarAsync(int d, string tipo, string msg, int? de, CancellationToken ct) { _s.Avisos.Add((d, tipo, msg)); return Task.CompletedTask; }
            public Task EventoAsync(string tipo, object detalhe, int? usuarioId, CancellationToken ct) { _i.Eventos.Add((tipo, usuarioId, System.Text.Json.JsonSerializer.Serialize(detalhe))); return Task.CompletedTask; }

            public Task<long> ConfirmarAsync(CancellationToken ct)
            {
                if (_alterou) _i.Versao++;
                Liberar();
                return Task.FromResult(_i.Versao);
            }

            private void Liberar() { if (!_livre) { _livre = true; _i.Trava.Release(); } }
            public ValueTask DisposeAsync() { Liberar(); return ValueTask.CompletedTask; }
        }
    }

    private static (RelacionamentoService Servico, Store Store, RelogioFalso Relogio) Montar()
    {
        var relogio = new RelogioFalso();
        var store = new Store(relogio);
        return (new RelacionamentoService(store, relogio), store, relogio);
    }

    private static async Task<RelacionamentoException> Falha(Func<Task> acao)
    {
        var ex = await Assert.ThrowsAsync<RelacionamentoException>(acao);
        return ex;
    }

    // ---------------------------------------------------------------- regras puras

    [Fact]
    public void Nome_sem_acento_e_sem_pontuacao_para_comparar()
    {
        Assert.Equal("padaria sao joao", RelacionamentoRegras.NormalizarNome("  Padaria  São João! "));
        Assert.Equal(1, RelacionamentoRegras.Similaridade("Açaí da Praça", "acai da praca"));
        Assert.True(RelacionamentoRegras.Similaridade("Pizzaria do Zé", "Pizzaria do Ze Centro") >= RelacionamentoRegras.LimiarParecido);
        Assert.True(RelacionamentoRegras.Similaridade("Padaria Sol", "Oficina Lua") < RelacionamentoRegras.LimiarParecido);
    }

    [Theory]
    [InlineData("+5534991480112", "+5534991480112")]
    [InlineData("+55 (34) 99148-0112", "+5534991480112")]
    [InlineData("34991480112", null)]
    [InlineData("+0123456789", null)]
    [InlineData("", null)]
    public void Whatsapp_so_vale_em_e164_com_mais_e_pais(string entrada, string? esperado) =>
        Assert.Equal(esperado, RelacionamentoRegras.TelefoneE164(entrada));

    [Fact]
    public void Aguardando_resposta_so_depois_de_envio_efetivo_sem_recebimento_posterior()
    {
        Assert.False(RelacionamentoRegras.CalcularEspera(null, null).Aguardando);
        Assert.False(RelacionamentoRegras.CalcularEspera(null, T0).Aguardando);
        Assert.True(RelacionamentoRegras.CalcularEspera(T0, null).Aguardando);
        Assert.True(RelacionamentoRegras.CalcularEspera(T0, T0.AddMinutes(-5)).Aguardando);
        Assert.False(RelacionamentoRegras.CalcularEspera(T0, T0.AddMinutes(5)).Aguardando);
    }

    [Fact]
    public void Dia_civil_e_o_de_Brasilia()
    {
        Assert.Equal(new DateOnly(2026, 11, 2), RelacionamentoRegras.Hoje(new DateTimeOffset(2026, 11, 3, 2, 30, 0, TimeSpan.Zero)));
    }

    // ---------------------------------------------------------------- lead

    private sealed class LeadStoreFalso : ILeadStore
    {
        public readonly List<LeadCandidatoBruto> Candidatos = new();
        public readonly Dictionary<string, LeadGravado> PorChave = new();
        public readonly List<LeadNovo> Criados = new();
        public bool Equipe = true;

        public Task<UsuarioEquipeDto?> MembroDaEquipeAsync(int usuarioId, CancellationToken ct) =>
            Task.FromResult<UsuarioEquipeDto?>(Equipe && usuarioId == 1 ? new UsuarioEquipeDto(1, "Farley") : null);

        public Task<IReadOnlyList<LeadCandidatoBruto>> CandidatosAsync(string nome, string? e164, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LeadCandidatoBruto>>(Candidatos.ToList());

        public Task<LeadGravado?> LeadPorChaveAsync(string chave, CancellationToken ct) => Task.FromResult(PorChave.GetValueOrDefault(chave));

        public Task<LeadGravado> CriarLeadAsync(LeadNovo novo, CancellationToken ct)
        {
            Criados.Add(novo);
            var g = new LeadGravado { OportunidadeId = Guid.NewGuid(), AcompanhamentoId = Guid.NewGuid(), ProximoPassoId = Guid.NewGuid(), Hash = novo.Hash };
            if (novo.Chave != null) PorChave[novo.Chave] = new LeadGravado { OportunidadeId = g.OportunidadeId, AcompanhamentoId = g.AcompanhamentoId, ProximoPassoId = g.ProximoPassoId, Hash = novo.Hash, Repetida = true };
            return Task.FromResult(g);
        }
    }

    private sealed class CnpjFalso : ICnpjFonte
    {
        public bool Disponivel => true;
        public Task<CnpjDados?> ConsultarAsync(string cnpj, CancellationToken ct) => Task.FromResult<CnpjDados?>(new CnpjDados("PADARIA SOL LTDA", "Padaria Sol", "ATIVA"));
    }

    private static CriarCrmLeadRequest LeadValido() => new()
    {
        Nome = "Padaria Sol", Whatsapp = "+5534991480112", Origem = "indicacao", Decisor = "Dona Maria", ResponsavelUsuarioId = 1,
        ProximoPasso = new CrmLeadProximoPassoRequest { Tipo = "ligacao", Data = Hoje.AddDays(1) }
    };

    private static LeadCriador Criador(LeadStoreFalso store, ICnpjFonte? cnpj = null) => new(store, new RelogioFalso(), cnpj);

    [Fact]
    public async Task Lead_sem_proximo_passo_devolve_422_e_nao_grava()
    {
        var store = new LeadStoreFalso();
        var req = LeadValido();
        req.ProximoPasso = null;
        var ex = await Falha(() => Criador(store).CriarAsync(req, 1, null));
        Assert.Equal(422, ex.Status);
        Assert.Equal("PROXIMO_PASSO_OBRIGATORIO", ex.Code);
        Assert.Empty(store.Criados);

        req.ProximoPasso = new CrmLeadProximoPassoRequest { Tipo = "ligacao", Data = Hoje.AddDays(-1) };
        Assert.Equal("PROXIMO_PASSO_NO_PASSADO", (await Falha(() => Criador(store).CriarAsync(req, 1, null))).Code);
    }

    [Fact]
    public async Task Lead_exige_nome_whatsapp_origem_decisor_e_responsavel_real()
    {
        var store = new LeadStoreFalso();
        var req = LeadValido();
        req.Nome = " "; req.Whatsapp = "34991480112"; req.Origem = null; req.Decisor = ""; req.ResponsavelUsuarioId = 99;
        var ex = await Falha(() => Criador(store).CriarAsync(req, 1, null));
        Assert.Equal(422, ex.Status);
        var campos = Assert.IsType<Dictionary<string, List<string>>>(ex.Details);
        Assert.Contains("nome", campos.Keys);
        Assert.Contains("whatsapp", campos.Keys);
        Assert.Contains("origem", campos.Keys);
        Assert.Contains("decisor", campos.Keys);
        Assert.Contains("responsavelUsuarioId", campos.Keys);
        Assert.Empty(store.Criados);
    }

    [Fact]
    public async Task Lead_valido_e_criado_com_responsavel_e_proximo_passo_e_sem_enriquecimento_quando_nao_ha_fonte()
    {
        var store = new LeadStoreFalso();
        var req = LeadValido();
        req.Cnpj = "11.222.333/0001-81";
        var lead = await Criador(store).CriarAsync(req, 1, null);
        Assert.Single(store.Criados);
        Assert.Equal("+5534991480112", lead.WhatsappE164);
        Assert.Equal("ligacao", lead.ProximoPasso!.Tipo);
        Assert.Equal(1, lead.Responsavel!.Id);
        Assert.True(lead.Enriquecimento.Solicitado);
        Assert.False(lead.Enriquecimento.Disponivel);
        Assert.Null(lead.Enriquecimento.RazaoSocial);
    }

    [Fact]
    public async Task Cnpj_so_enriquece_quando_a_fonte_esta_disponivel()
    {
        var store = new LeadStoreFalso();
        var req = LeadValido();
        req.Cnpj = "11.222.333/0001-81";
        var lead = await Criador(store, new CnpjFalso()).CriarAsync(req, 1, null);
        Assert.True(lead.Enriquecimento.Disponivel);
        Assert.Equal("PADARIA SOL LTDA", lead.Enriquecimento.RazaoSocial);
    }

    [Fact]
    public async Task Cnpj_invalido_e_rejeitado_mas_em_branco_e_aceito()
    {
        var store = new LeadStoreFalso();
        var req = LeadValido();
        req.Cnpj = "12.345.678/0001-00";
        Assert.Equal("VALIDACAO", (await Falha(() => Criador(store).CriarAsync(req, 1, null))).Code);
        req.Cnpj = null;
        Assert.False((await Criador(store).CriarAsync(req, 1, null)).Enriquecimento.Solicitado);
    }

    [Fact]
    public async Task Parecido_por_nome_sem_acento_ou_telefone_devolve_409_com_candidatos_e_nao_cria()
    {
        var store = new LeadStoreFalso();
        var existente = Guid.NewGuid();
        store.Candidatos.Add(new LeadCandidatoBruto { Id = existente, Nome = "PADARIA SOL", Telefone = "+5511999990000", Situacao = "ativo/conversa" });
        store.Candidatos.Add(new LeadCandidatoBruto { Id = Guid.NewGuid(), Nome = "Oficina do Joao", Telefone = "+5534991480112", Situacao = "ativo/conversa" });
        store.Candidatos.Add(new LeadCandidatoBruto { Id = Guid.NewGuid(), Nome = "Mecanica Lua", Telefone = "+5511888880000", Situacao = "ativo/conversa" });

        var ex = await Falha(() => Criador(store).CriarAsync(LeadValido(), 1, null));
        Assert.Equal(409, ex.Status);
        Assert.Equal("DUPLICADO", ex.Code);
        Assert.Empty(store.Criados);

        var candidatos = ((IReadOnlyList<CrmLeadCandidatoDto>)ex.Details!.GetType().GetProperty("candidatos")!.GetValue(ex.Details)!).ToList();
        Assert.Equal(2, candidatos.Count);
        Assert.Contains(candidatos, c => c.Id == existente && c.Motivos.Contains("nome_igual"));
        Assert.Contains(candidatos, c => c.Motivos.Contains("telefone_igual"));
        Assert.DoesNotContain(candidatos, c => c.Nome == "Mecanica Lua");
    }

    [Fact]
    public async Task Criar_mesmo_com_parecido_exige_decisao_motivo_e_conferencia_de_todos_os_candidatos()
    {
        var store = new LeadStoreFalso();
        var a = Guid.NewGuid();
        store.Candidatos.Add(new LeadCandidatoBruto { Id = a, Nome = "Padaria Sol", Situacao = "ativo/conversa" });
        var req = LeadValido();
        req.DecisaoDuplicidade = "criar_novo";

        Assert.Equal("MOTIVO_DUPLICIDADE", (await Falha(() => Criador(store).CriarAsync(req, 1, null))).Code);

        req.MotivoDuplicidade = "Outra unidade, outro dono";
        Assert.Equal("DUPLICADO", (await Falha(() => Criador(store).CriarAsync(req, 1, null))).Code); // nao conferiu o candidato
        Assert.Empty(store.Criados);

        req.CandidatosConfirmados = new List<Guid> { a };
        var lead = await Criador(store).CriarAsync(req, 1, null);
        Assert.True(lead.DuplicidadeDecidida);
        var gravado = Assert.Single(store.Criados);
        Assert.Contains("Outra unidade", gravado.DecisaoDuplicidade);

        store.Candidatos.Add(new LeadCandidatoBruto { Id = Guid.NewGuid(), Nome = "Padaria Sol Centro", Situacao = "ativo/conversa" }); // surgiu depois
        Assert.Equal("DUPLICADO", (await Falha(() => Criador(store).CriarAsync(req, 1, null))).Code);
    }

    [Fact]
    public async Task Mesma_chave_e_mesmos_dados_devolve_o_mesmo_lead_sem_criar_outro_e_dados_diferentes_dao_409()
    {
        var store = new LeadStoreFalso();
        var primeiro = await Criador(store).CriarAsync(LeadValido(), 1, "chave-1");
        var segundo = await Criador(store).CriarAsync(LeadValido(), 1, "chave-1");
        Assert.Single(store.Criados);
        Assert.True(segundo.Repetida);
        Assert.Equal(primeiro.OportunidadeId, segundo.OportunidadeId);

        var outro = LeadValido();
        outro.Origem = "anuncio";
        var ex = await Falha(() => Criador(store).CriarAsync(outro, 1, "chave-1"));
        Assert.Equal((409, "CHAVE_REUTILIZADA"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task Buscar_parecidos_exige_nome_ou_telefone_e_telefone_valido()
    {
        var store = new LeadStoreFalso();
        Assert.Equal("BUSCA_VAZIA", (await Falha(() => Criador(store).ParecidosAsync(null, null))).Code);
        Assert.Equal("WHATSAPP_INVALIDO", (await Falha(() => Criador(store).ParecidosAsync(null, "abc"))).Code);
        store.Candidatos.Add(new LeadCandidatoBruto { Id = Guid.NewGuid(), Nome = "Padaria Sol" });
        Assert.Single(await Criador(store).ParecidosAsync("padaria sol", null));
    }

    // ---------------------------------------------------------------- passar / assumir

    [Fact]
    public async Task Passar_troca_responsavel_avisa_o_antigo_mantem_visao_por_7_dias_e_leva_a_pendencia()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 2);
        await s.DefinirProximoPassoAsync(loja.Id, new DefinirProximoPassoRequest { Tipo = "reuniao", Data = Hoje.AddDays(2) }, 2);

        var r = await s.PassarAsync(new PassarRequest { LojaIds = { loja.Id }, ParaUsuarioId = 3, Motivo = "ferias" }, 1);

        Assert.Equal(1, r.Passadas);
        Assert.Equal("passada", r.Itens[0].Status);
        Assert.Equal(3, loja.Responsavel);
        Assert.Equal(2, loja.Anterior);
        Assert.Equal(T0.AddDays(7), loja.Ate);
        Assert.Equal("reuniao", loja.Passo!.Tipo); // a pendencia continua e agora e do novo
        Assert.Contains(store.Avisos, a => a.Destinatario == 2 && a.Tipo == "responsavel_trocou" && a.Mensagem.Contains("ate 09/11"));
        Assert.Contains(store.Avisos, a => a.Destinatario == 3 && a.Tipo == "recebeu_loja" && a.Mensagem.Contains("reuniao"));

        var dto = await s.ObterAsync(loja.Id);
        Assert.Equal(2, dto.VisaoAnterior!.UsuarioId);
    }

    [Fact]
    public async Task Visao_do_antigo_expira_depois_de_7_dias()
    {
        var (s, store, relogio) = Montar();
        var loja = store.Novo(responsavel: 2);
        await s.PassarAsync(new PassarRequest { LojaIds = { loja.Id }, ParaUsuarioId = 3 }, 1);
        relogio.Agora = T0.AddDays(7).AddMinutes(1);
        Assert.Null((await s.ObterAsync(loja.Id)).VisaoAnterior);
        var equipe = await s.EquipeAsync(2);
        Assert.Null(equipe.Lojas.Single().VisaoTransitoriaUsuarioId);
    }

    [Fact]
    public async Task Passar_nao_reescreve_historico_so_acrescenta_eventos()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        await s.PassarAsync(new PassarRequest { LojaIds = { loja.Id }, ParaUsuarioId = 2 }, 1);
        var depoisDoPrimeiro = loja.Eventos.ToList();
        await s.PassarAsync(new PassarRequest { LojaIds = { loja.Id }, ParaUsuarioId = 3 }, 1);
        Assert.Equal(2, loja.Eventos.Count);
        Assert.Equal(depoisDoPrimeiro[0], loja.Eventos[0]);
        Assert.Contains("\"de\":1", loja.Eventos[0].Detalhe);
        Assert.Contains("\"de\":2", loja.Eventos[1].Detalhe);
    }

    [Fact]
    public async Task Passar_em_lote_reporta_cada_loja_e_nao_para_no_erro()
    {
        var (s, store, _) = Montar();
        var a = store.Novo(responsavel: 1);
        var b = store.Novo(responsavel: 3); // ja e do destino
        var inexistente = Guid.NewGuid();
        var r = await s.PassarAsync(new PassarRequest { LojaIds = { a.Id, b.Id, inexistente, a.Id }, ParaUsuarioId = 3 }, 1);
        Assert.Equal(3, r.Itens.Count); // duplicado some
        Assert.Equal(1, r.Passadas);
        Assert.Equal(new[] { "passada", "sem_mudanca", "erro" }, r.Itens.Select(i => i.Status));
        Assert.Equal("NAO_ENCONTRADO", r.Itens[2].Codigo);
    }

    [Fact]
    public async Task Passar_valida_lote_destinatario_e_permissao()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo();
        Assert.Equal("LOTE_VAZIO", (await Falha(() => s.PassarAsync(new PassarRequest { ParaUsuarioId = 2 }, 1))).Code);
        Assert.Equal("LOTE_GRANDE", (await Falha(() => s.PassarAsync(new PassarRequest
            { ParaUsuarioId = 2, LojaIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList() }, 1))).Code);
        Assert.Equal("DESTINATARIO_INVALIDO", (await Falha(() => s.PassarAsync(new PassarRequest { LojaIds = { loja.Id }, ParaUsuarioId = 99 }, 1))).Code);
        var semPermissao = await Falha(() => s.PassarAsync(new PassarRequest { LojaIds = { loja.Id }, ParaUsuarioId = 2 }, 77));
        Assert.Equal((403, "SEM_PERMISSAO"), (semPermissao.Status, semPermissao.Code));
        Assert.Null(loja.Responsavel);
    }

    [Fact]
    public async Task Passagens_simultaneas_com_a_mesma_versao_so_uma_vence()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        var versao = new Dictionary<Guid, long> { [loja.Id] = loja.Versao };
        var tarefas = new[] { 2, 3 }.Select(destino => Task.Run(() =>
            s.PassarAsync(new PassarRequest { LojaIds = { loja.Id }, ParaUsuarioId = destino, VersoesEsperadas = versao }, 1))).ToArray();
        var resultados = await Task.WhenAll(tarefas);
        var itens = resultados.Select(r => r.Itens[0]).ToList();
        Assert.Equal(1, itens.Count(i => i.Status == "passada"));
        Assert.Equal(1, itens.Count(i => i.Codigo == "VERSAO_MUDOU"));
        Assert.Equal(2, loja.Versao);
        Assert.Single(loja.Eventos);
    }

    [Fact]
    public async Task Assumir_e_idempotente_e_respeita_a_versao_vista()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 2);
        var dto = await s.AssumirAsync(loja.Id, new AssumirRequest { VersaoEsperada = 1 }, 1);
        Assert.Equal(1, dto.Responsavel!.Id);
        Assert.False(dto.Repetida);
        Assert.Contains(store.Avisos, a => a.Destinatario == 2 && a.Tipo == "responsavel_trocou");

        var denovo = await s.AssumirAsync(loja.Id, null, 1);
        Assert.True(denovo.Repetida);
        Assert.Single(loja.Eventos);

        var ex = await Falha(() => s.AssumirAsync(loja.Id, new AssumirRequest { VersaoEsperada = 1 }, 3));
        Assert.Equal((409, "VERSAO_MUDOU"), (ex.Status, ex.Code));
        Assert.Equal(1, loja.Responsavel);
    }

    // ---------------------------------------------------------------- cobrar responsavel

    [Fact]
    public async Task Cobrar_avisa_o_responsavel_e_so_uma_vez_por_dia()
    {
        var (s, store, relogio) = Montar();
        var loja = store.Novo(responsavel: 2);
        await s.CobrarResponsavelAsync(loja.Id, new CobrarResponsavelRequest { Mensagem = "cliente esperando" }, 1);
        Assert.Contains(store.Avisos, a => a.Destinatario == 2 && a.Tipo == "cobranca" && a.Mensagem.Contains("cliente esperando"));

        var ex = await Falha(() => s.CobrarResponsavelAsync(loja.Id, null, 1));
        Assert.Equal((409, "COBRANCA_RECENTE"), (ex.Status, ex.Code));

        await s.CobrarResponsavelAsync(loja.Id, null, 3); // outra pessoa pode
        relogio.Agora = T0.AddHours(25);
        await s.CobrarResponsavelAsync(loja.Id, null, 1);
        Assert.Equal(3, loja.Cobrancas.Count);
    }

    [Fact]
    public async Task Cobrar_sem_responsavel_ou_a_si_mesmo_e_422()
    {
        var (s, store, _) = Montar();
        var semDono = store.Novo();
        var meu = store.Novo(responsavel: 1);
        Assert.Equal("SEM_RESPONSAVEL", (await Falha(() => s.CobrarResponsavelAsync(semDono.Id, null, 1))).Code);
        Assert.Equal("PROPRIO_RESPONSAVEL", (await Falha(() => s.CobrarResponsavelAsync(meu.Id, null, 1))).Code);
        Assert.Empty(store.Avisos);
    }

    // ---------------------------------------------------------------- proximo passo

    [Fact]
    public async Task Proximo_passo_exige_tipo_e_data_nao_passada_e_trocar_nao_apaga_o_anterior()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        Assert.Equal("PROXIMO_PASSO_OBRIGATORIO", (await Falha(() => s.DefinirProximoPassoAsync(loja.Id, new DefinirProximoPassoRequest { Tipo = "ligacao" }, 1))).Code);
        Assert.Equal("PROXIMO_PASSO_TIPO_INVALIDO", (await Falha(() => s.DefinirProximoPassoAsync(loja.Id, new DefinirProximoPassoRequest { Tipo = "telepatia", Data = Hoje }, 1))).Code);
        Assert.Equal("PROXIMO_PASSO_NO_PASSADO", (await Falha(() => s.DefinirProximoPassoAsync(loja.Id, new DefinirProximoPassoRequest { Tipo = "ligacao", Data = Hoje.AddDays(-1) }, 1))).Code);

        await s.DefinirProximoPassoAsync(loja.Id, new DefinirProximoPassoRequest { Tipo = "Ligacao", Data = Hoje }, 1);
        var repetido = await s.DefinirProximoPassoAsync(loja.Id, new DefinirProximoPassoRequest { Tipo = "ligacao", Data = Hoje }, 1);
        Assert.True(repetido.Repetida);
        Assert.Single(loja.Eventos);

        await s.DefinirProximoPassoAsync(loja.Id, new DefinirProximoPassoRequest { Tipo = "proposta", Data = Hoje.AddDays(3) }, 1);
        Assert.Equal(2, loja.Eventos.Count);
        Assert.Contains("\"Tipo\":\"ligacao\"", loja.Eventos[1].Detalhe); // o anterior fica registrado no evento
    }

    [Fact]
    public async Task Equipe_lista_carteira_vencidos_hoje_sem_responsavel_e_lembretes_ligados_ao_proximo_passo()
    {
        var (s, store, _) = Montar();
        var vencida = store.Novo(responsavel: 1, nome: "A");
        var hoje = store.Novo(responsavel: 1, nome: "B");
        var futura = store.Novo(responsavel: 2, nome: "C");
        var semDono = store.Novo(nome: "D");
        vencida.Passo = new ProximoPassoDto(Guid.NewGuid(), "ligacao", Hoje.AddDays(-2), null, 1, T0, false);
        hoje.Passo = new ProximoPassoDto(Guid.NewGuid(), "mensagem", Hoje, null, 1, T0, false);
        futura.Passo = new ProximoPassoDto(Guid.NewGuid(), "visita", Hoje.AddDays(5), null, 2, T0, false);
        hoje.Enviada = T0.AddHours(-3);

        var eq = await s.EquipeAsync(1);
        var farley = eq.Membros.Single(m => m.Id == 1);
        Assert.Equal((2, 1, 1, 1), (farley.Lojas, farley.PassosVencidos, farley.PassosHoje, farley.AguardandoResposta));
        Assert.Equal(1, eq.SemResponsavel);
        Assert.Equal(new[] { vencida.Passo.Id, hoje.Passo.Id }, eq.Lembretes.Select(l => l.ProximoPassoId));
        Assert.True(eq.Lembretes[0].Vencido);
        Assert.DoesNotContain(eq.Lembretes, l => l.AcompanhamentoId == futura.Id || l.AcompanhamentoId == semDono.Id);
    }

    // ---------------------------------------------------------------- contato real

    [Fact]
    public async Task Resposta_so_fica_aguardando_depois_de_envio_efetivo()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        Assert.False((await s.ObterAsync(loja.Id)).Espera.Aguardando);

        await Falha(() => s.RegistrarMarcoContatoAsync(loja.Id, DirecoesContato.Enviada, T0, " "));
        Assert.False((await s.ObterAsync(loja.Id)).Espera.Aguardando); // sem referencia da mensagem real nada acontece

        await s.RegistrarMarcoContatoAsync(loja.Id, DirecoesContato.Enviada, T0.AddMinutes(-10), "wamid.A");
        Assert.True((await s.ObterAsync(loja.Id)).Espera.Aguardando);
        await s.RegistrarMarcoContatoAsync(loja.Id, DirecoesContato.Recebida, T0.AddMinutes(-2), "wamid.B");
        Assert.False((await s.ObterAsync(loja.Id)).Espera.Aguardando);

        Assert.Equal("CONTATO_NO_FUTURO", (await Falha(() => s.RegistrarMarcoContatoAsync(loja.Id, DirecoesContato.Enviada, T0.AddHours(2), "wamid.C"))).Code);
        Assert.Equal("DIRECAO_INVALIDA", (await Falha(() => s.RegistrarMarcoContatoAsync(loja.Id, "rascunho", T0, "x"))).Code);
    }

    // ---------------------------------------------------------------- marcacao

    [Fact]
    public async Task Marcacao_exige_tipo_e_motivo_uma_ativa_por_vez_e_remocao_com_permissao()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 2);
        Assert.Equal("MARCACAO_TIPO_INVALIDO", (await Falha(() => s.MarcarAsync(loja.Id, new MarcarRequest { Tipo = "x", Motivo = "motivo ok" }, 1))).Code);
        Assert.Equal("MOTIVO_OBRIGATORIO", (await Falha(() => s.MarcarAsync(loja.Id, new MarcarRequest { Tipo = "atencao", Motivo = "ok" }, 1))).Code);

        await s.MarcarAsync(loja.Id, new MarcarRequest { Tipo = "atencao", Motivo = "pedidos cairam" }, 1);
        Assert.Equal("MARCACAO_JA_ATIVA", (await Falha(() => s.MarcarAsync(loja.Id, new MarcarRequest { Tipo = "atencao", Motivo = "de novo" }, 3))).Code);
        await s.MarcarAsync(loja.Id, new MarcarRequest { Tipo = "risco_de_saida", Motivo = "falou em cancelar" }, 1);
        Assert.Equal("risco_de_saida", loja.Marcacao!.Tipo);

        var semPermissao = await Falha(() => s.DesmarcarAsync(loja.Id, "resolvido", 3)); // nem autora nem responsavel
        Assert.Equal((403, "SEM_PERMISSAO"), (semPermissao.Status, semPermissao.Code));
        Assert.Equal("MOTIVO_OBRIGATORIO", (await Falha(() => s.DesmarcarAsync(loja.Id, null, 1))).Code);

        await s.DesmarcarAsync(loja.Id, "ja resolvido", 2); // o responsavel pode
        Assert.Null(loja.Marcacao);
        Assert.Equal("SEM_MARCACAO", (await Falha(() => s.DesmarcarAsync(loja.Id, "ja resolvido", 2))).Code);
        Assert.Equal(new[] { "marcou", "marcou", "desmarcou" }, loja.Eventos.Select(e => e.Tipo));
    }

    // ---------------------------------------------------------------- cancelamento / retencao

    private static CancelamentoRequest Cancelamento(Action<CancelamentoRequest>? ajusta = null)
    {
        var r = new CancelamentoRequest
        {
            ChaveIdempotencia = "cancel-1", Pedido = "quer cancelar o plano", PedidoEm = T0.AddHours(-1), Leitura = "achou caro",
            Canal = "whatsapp", Resultado = "retida", Oferta = new OfertaRetencaoRequest { Tipo = "nenhuma" }
        };
        ajusta?.Invoke(r);
        return r;
    }

    [Fact]
    public async Task Cancelamento_registra_pedido_leitura_oferta_canal_e_resultado_sem_executar_nada_financeiro()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 2);
        var r = await s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c =>
            c.Oferta = new OfertaRetencaoRequest { Tipo = "desconto", Valor = 50, Aprovada = true, Descricao = "50 por 2 meses" }), 1);
        Assert.Equal("retida", r.Resultado);
        Assert.Equal("desconto", r.Oferta.Tipo);
        Assert.Equal(1, r.Oferta.AprovadaPor);
        Assert.False(r.ExecutouFinanceiro);
        Assert.Contains(loja.Eventos, e => e.Tipo == "retencao_registrada");
        Assert.Contains(store.Avisos, a => a.Destinatario == 2 && a.Tipo == "retencao");
    }

    [Fact]
    public async Task Desconto_ou_estorno_sem_aprovacao_humana_e_sem_valor_sao_recusados()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        var sem = await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => c.Oferta = new OfertaRetencaoRequest { Tipo = "estorno", Valor = 100 }), 1));
        Assert.Equal("OFERTA_NAO_APROVADA", sem.Code);
        var semValor = await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => c.Oferta = new OfertaRetencaoRequest { Tipo = "desconto", Aprovada = true }), 1));
        Assert.Equal("OFERTA_SEM_VALOR", semValor.Code);
        Assert.Empty(loja.Retencoes);
        Assert.Empty(loja.Eventos);
    }

    [Fact]
    public async Task Cancelamento_repetido_com_a_mesma_chave_devolve_o_registro_sem_duplicar()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        var primeiro = await s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(), 1);
        var primeiroRepetida = primeiro.Repetida; // o fake devolve o mesmo objeto; o store real devolve outro a cada leitura
        var segundo = await s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(), 1);
        Assert.False(primeiroRepetida);
        Assert.True(segundo.Repetida);
        Assert.Equal(primeiro.Id, segundo.Id);
        Assert.Single(loja.Retencoes);
        Assert.Single(loja.Eventos);
    }

    [Fact]
    public async Task Cancelamento_exige_chave_pedido_leitura_canal_e_resultado()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        Assert.Equal("CHAVE_OBRIGATORIA", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => c.ChaveIdempotencia = null), 1))).Code);
        Assert.Equal("PEDIDO_OBRIGATORIO", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => c.Pedido = " "), 1))).Code);
        Assert.Equal("LEITURA_OBRIGATORIA", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => c.Leitura = null), 1))).Code);
        Assert.Equal("CANAL_INVALIDO", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => c.Canal = "pombo"), 1))).Code);
        Assert.Equal("RESULTADO_INVALIDO", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => c.Resultado = "talvez"), 1))).Code);
        Assert.Equal("PEDIDO_NO_FUTURO", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => c.PedidoEm = T0.AddDays(1)), 1))).Code);
    }

    [Fact]
    public async Task Saida_calcula_a_data_pela_politica_escolhida()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);

        var agora = await s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "a"; c.Resultado = "saiu"; c.SaidaQuando = "agora"; }), 1);
        Assert.Equal(Hoje, agora.SaidaEm);

        var aviso = await s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "b"; c.Resultado = "saiu"; c.SaidaQuando = "aviso_30_dias"; }), 1);
        Assert.Equal(Hoje.AddDays(30), aviso.SaidaEm); // pedido foi hoje as 11:00 em Brasilia

        var ciclo = await s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "c"; c.Resultado = "saiu"; c.SaidaQuando = "fim_do_ciclo"; c.SaidaEm = Hoje.AddDays(12); }), 1);
        Assert.Equal(Hoje.AddDays(12), ciclo.SaidaEm);

        Assert.Equal("SAIDA_SEM_DATA", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "d"; c.Resultado = "saiu"; c.SaidaQuando = "fim_do_ciclo"; }), 1))).Code);
        Assert.Equal("SAIDA_ANTES_DO_AVISO", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "e"; c.Resultado = "saiu"; c.SaidaQuando = "aviso_30_dias"; c.SaidaEm = Hoje.AddDays(5); }), 1))).Code);
        Assert.Equal("SAIDA_QUANDO_INVALIDO", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "f"; c.Resultado = "saiu"; }), 1))).Code);
        Assert.Contains(loja.Eventos, e => e.Tipo == "saida_registrada");
    }

    [Fact]
    public async Task Pausa_respeita_a_politica_de_90_dias_e_nao_vale_com_cobranca_em_aberto()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        Assert.Equal("PAUSA_SEM_DATA", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "p1"; c.Resultado = "pausada"; }), 1))).Code);
        Assert.Equal("PAUSA_NO_PASSADO", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "p2"; c.Resultado = "pausada"; c.PausaAte = Hoje; }), 1))).Code);
        Assert.Equal("PAUSA_ACIMA_DA_POLITICA", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "p3"; c.Resultado = "pausada"; c.PausaAte = Hoje.AddDays(91); }), 1))).Code);

        loja.CobrancaAberta = true;
        Assert.Equal("PAUSA_COM_COBRANCA_EM_ABERTO", (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "p4"; c.Resultado = "pausada"; c.PausaAte = Hoje.AddDays(30); }), 1))).Code);
        loja.CobrancaAberta = false;
        var ok = await s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.ChaveIdempotencia = "p5"; c.Resultado = "pausada"; c.PausaAte = Hoje.AddDays(30); }), 1);
        Assert.Equal(Hoje.AddDays(30), ok.PausaAte);
    }

    [Fact]
    public async Task Saida_com_cobranca_em_aberto_registra_o_fato_sem_cancelar_a_cobranca()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        loja.CobrancaAberta = true;
        var r = await s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(c => { c.Resultado = "saiu"; c.SaidaQuando = "agora"; }), 1);
        Assert.True(r.CobrancaEmAberto);
        Assert.False(r.ExecutouFinanceiro);
    }

    [Fact]
    public async Task Cancelamento_so_pela_equipe_e_loja_inexistente_e_404()
    {
        var (s, store, _) = Montar();
        var loja = store.Novo(responsavel: 1);
        Assert.Equal(403, (await Falha(() => s.RegistrarCancelamentoAsync(loja.Id, Cancelamento(), 99))).Status);
        Assert.Equal(404, (await Falha(() => s.RegistrarCancelamentoAsync(Guid.NewGuid(), Cancelamento(), 1))).Status);
    }
}
