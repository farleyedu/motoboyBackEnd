using System.Net;
using System.Text;
using System.Text.Json;
using APIBack.Social.Ia;
using APIBack.Social.Nucleo;
using APIBack.Social.Voz;
using Xunit;

namespace APIBack.Tests.Unit.Social;

public sealed class SocialVozTests
{
    private static SocialException Falha(Action acao) => Assert.Throws<SocialException>(acao);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 1)]
    [InlineData(30, 1)]
    [InlineData(30.01, 2)]
    [InlineData(61, 3)]
    public void Voz_cobra_1_credito_a_cada_30_segundos(decimal segundos, int creditos) =>
        Assert.Equal(creditos, CreditosRegras.Voz(segundos, 30));

    [Fact]
    public void Duracao_estimada_considera_a_velocidade_e_tem_minimo()
    {
        var texto = string.Join(' ', Enumerable.Repeat("palavra", 26));
        Assert.Equal(10m, CreditosRegras.SegundosEstimados(texto, 1));
        Assert.Equal(5m, CreditosRegras.SegundosEstimados(texto, 2));
        Assert.Equal(0.5m, CreditosRegras.SegundosEstimados("oi", 1));
    }

    [Fact]
    public void Hash_da_fala_muda_com_texto_voz_velocidade_provedor_e_modelo()
    {
        var a = CreditosRegras.HashFala("Olá", "nova", 1, "openai", "tts-1");
        Assert.Equal(a, CreditosRegras.HashFala(" Olá ", "nova", 1.00m, "openai", "tts-1"));
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.NotEqual(a, CreditosRegras.HashFala("Olá!", "nova", 1, "openai", "tts-1"));
        Assert.NotEqual(a, CreditosRegras.HashFala("Olá", "onyx", 1, "openai", "tts-1"));
        Assert.NotEqual(a, CreditosRegras.HashFala("Olá", "nova", 1.1m, "openai", "tts-1"));
        Assert.NotEqual(a, CreditosRegras.HashFala("Olá", "nova", 1, "elevenlabs", "tts-1"));
        Assert.NotEqual(a, CreditosRegras.HashFala("Olá", "nova", 1, "openai", "gpt-4o-mini-tts"));
    }

    [Fact]
    public void Saldo_nunca_negativo_e_avisa_aos_80_por_cento()
    {
        Assert.Equal(0, new SaldoCreditos(10, 0, 12).Restam);
        Assert.False(new SaldoCreditos(100, 0, 79).Acima80);
        Assert.True(new SaldoCreditos(90, 10, 80).Acima80);
        Assert.False(new SaldoCreditos(0, 0, 0).Acima80);
    }

    [Fact]
    public void Limite_por_minuto_da_chave_da_zippy_e_por_loja()
    {
        var relogio = new Relogio();
        var limite = new LimiteIaZippy(relogio);
        var a = Guid.NewGuid();
        Assert.True(limite.Tentar(a, 2));
        Assert.True(limite.Tentar(a, 2));
        Assert.False(limite.Tentar(a, 2));
        Assert.True(limite.Tentar(Guid.NewGuid(), 2));
        relogio.Avancar(TimeSpan.FromSeconds(61));
        Assert.True(limite.Tentar(a, 2));
    }

    [Fact]
    public void Wav_e_mp3_tem_a_duracao_lida_sem_decodificar()
    {
        Assert.Equal(2.5m, AudioInfo.Duracao(AudioInfo.WavSilencio(2.5m), "audio/wav"));
        // 100 quadros MPEG-1 Layer III, 128 kbps, 44,1 kHz: 100 × 1152 / 44100 = 2,61 s. Com ID3 na frente.
        var quadro = new byte[417];
        quadro[0] = 0xFF; quadro[1] = 0xFB; quadro[2] = 0x90; quadro[3] = 0x00;
        var id3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5 };
        var mp3 = id3.Concat(Enumerable.Range(0, 100).SelectMany(_ => quadro)).ToArray();
        Assert.Equal(2.61m, AudioInfo.Duracao(mp3, "audio/mpeg"));
        Assert.Null(AudioInfo.Duracao(Encoding.ASCII.GetBytes("não é áudio"), "audio/webm"));
    }

    [Fact]
    public void Alinhamento_por_caractere_vira_tempo_por_palavra()
    {
        var chars = "Oi, tudo bem".Select(c => c.ToString()).ToList();
        var ini = Enumerable.Range(0, chars.Count).Select(i => i * 0.1m).ToList();
        var fim = ini.Select(i => i + 0.1m).ToList();
        var palavras = Alinhamento.DeCaracteres(chars, ini, fim);
        Assert.Equal(new[] { "Oi,", "tudo", "bem" }, palavras.Select(p => p.Texto));
        Assert.Equal(0m, palavras[0].Ini);
        Assert.Equal(0.3m, palavras[0].Fim);
        Assert.Equal(0.4m, palavras[1].Ini);
        Assert.Equal(1.2m, palavras[2].Fim);
    }

    [Fact]
    public void Tempo_estimado_das_palavras_cobre_a_fala_inteira_em_ordem()
    {
        var p = Alinhamento.Estimar("e foi assim que acabou", 3m);
        Assert.Equal(5, p.Count);
        Assert.Equal(0m, p[0].Ini);
        Assert.Equal(3m, p[^1].Fim);
        for (var i = 1; i < p.Count; i++) Assert.Equal(p[i - 1].Fim, p[i].Ini);
        Assert.Empty(Alinhamento.Estimar("   ", 3));
    }

    [Fact]
    public void Gerar_valida_vozes_ids_e_nao_mistura_clone_com_ia()
    {
        static FalaGerarRequest F(string id, string texto, string voz, decimal? vel) => new(id, texto, voz, vel);
        static GerarVozRequest R(params FalaGerarRequest[] f) => new(9, f.ToList());
        var (falas, clone) = VozRegras.Gerar(R(F("a", " Olá ", "FEM", null), F("b", "Tchau", "doc", 1.2m)));
        Assert.False(clone);
        Assert.Equal("Olá", falas[0].Texto);
        Assert.Equal("fem", falas[0].Voz);
        Assert.Equal(1m, falas[0].Vel);
        Assert.True(VozRegras.Gerar(R(F("a", "Oi", "clone", 1))).Clone);

        Assert.Equal("VALIDACAO", Falha(() => VozRegras.Gerar(R(F("a", "Oi", "robo", 1)))).Codigo);
        Falha(() => VozRegras.Gerar(R(F("a", "Oi", "fem", 1), F("a", "Oi", "fem", 1))));
        Falha(() => VozRegras.Gerar(R(F("a", "Oi", "fem", 1), F("b", "Oi", "clone", 1))));
        Falha(() => VozRegras.Gerar(R(F("a", "Oi", "fem", 3))));
        Falha(() => VozRegras.Gerar(R(F("a", "", "fem", 1))));
        Falha(() => VozRegras.Gerar(R(F("a", new string('x', 1001), "fem", 1))));
        Falha(() => VozRegras.Gerar(new GerarVozRequest(0, new() { new("a", "Oi", "fem", 1) })));
        Falha(() => VozRegras.Gerar(R(Enumerable.Range(0, 41).Select(i => new FalaGerarRequest($"f{i}", "Oi", "fem", 1)).ToArray())));
    }

    [Fact]
    public void Voz_clonada_exige_aceite_texto_lido_e_60_a_90_segundos()
    {
        var wav75 = AudioInfo.WavSilencio(75m);
        ClonarVozRequest R(byte[] b, bool aceite = true, string? lido = "Eu sou a dona desta voz e autorizo", decimal? dur = null, string mime = "audio/wav") =>
            new(b, mime, "a.wav", aceite, lido, dur, "1.2.3.4", "teste");

        Assert.Equal(75m, VozRegras.DuracaoClone(R(wav75)));
        Assert.Equal("CONSENTIMENTO_OBRIGATORIO", Falha(() => VozRegras.DuracaoClone(R(wav75, aceite: false))).Codigo);
        Assert.Equal("CONSENTIMENTO_OBRIGATORIO", Falha(() => VozRegras.DuracaoClone(R(wav75, lido: "curto"))).Codigo);
        Assert.Contains("60 a 90", Falha(() => VozRegras.DuracaoClone(R(AudioInfo.WavSilencio(30m)))).Message);
        Assert.Contains("60 a 90", Falha(() => VozRegras.DuracaoClone(R(AudioInfo.WavSilencio(95m)))).Message);
        // WebM do navegador: a duração vem do formulário.
        var webm = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3 };
        Assert.Equal(80m, VozRegras.DuracaoClone(R(webm, dur: 80, mime: "audio/webm")));
        Falha(() => VozRegras.DuracaoClone(R(webm, mime: "audio/webm")));
        Falha(() => VozRegras.DuracaoClone(R(webm, dur: 80, mime: "video/webm")));
    }

    [Fact]
    public void Termo_da_voz_clonada_fala_so_da_propria_voz()
    {
        Assert.Contains("minha própria voz", TermoVozClonada.Texto);
        Assert.Contains("não de outra pessoa, personagem ou dublador", TermoVozClonada.Texto);
        Assert.Contains("rascunho", TermoVozClonada.Versao);
    }

    [Fact]
    public void Roteiro_le_o_corte_e_so_aceita_ancoras_das_marcas()
    {
        var corte = TextosRegras.LerCorte("Fuga", 40, """
            {"eventos":["explosao","risada"],"momentos":[{"id":"m1","k":"tiro","t":3}],
             "falas":[{"quem":"Lucas","t":1.5,"txt":"olha isso"},{"txt":""}]}
            """);
        Assert.Equal(new[] { "m1", "explosao", "risada" }, corte.Marcas);
        Assert.Equal(new[] { "1.5s Lucas: olha isso" }, corte.Falas);
        Assert.Contains("explosao", TextosRegras.Contexto(corte));

        var json = TextosRegras.Json("""
            ```json
            {"falas":[{"texto":"Tudo começou aqui","ancora":"inicio"},{"texto":"Agora explode","ancora":"antes:explosao"},
                      {"texto":"Isso não existe","ancora":"depois:inventada"},{"texto":"Fim","ancora":"xpto"},{"texto":"sobra","ancora":"fim"}]}
            ```
            """, "anthropic");
        var falas = TextosRegras.Falas(json, corte.Marcas, "anthropic");
        Assert.Equal(4, falas.Count);
        Assert.Equal(new[] { "inicio", "antes:explosao", "inicio", "fim" }, falas.Select(f => f.Ancora));
        Assert.All(falas, f => Assert.Equal(8, f.Id.Length));

        Assert.Equal(TipoFalhaIa.ForaDoAr, Assert.Throws<FalhaIaException>(() => TextosRegras.Json("sem json", "x")).Tipo);
        Assert.Throws<FalhaIaException>(() => TextosRegras.Falas(TextosRegras.Json("""{"falas":[]}""", "x"), corte.Marcas, "x"));
        Assert.Equal("um dois três", TextosRegras.Limitar("um dois  três quatro", 3));
    }

    [Fact]
    public async Task Texto_falso_responde_no_formato_de_cada_pedido()
    {
        var falso = new TextoFalso();
        async Task<JsonElement> R(string pedido) =>
            TextosRegras.Json((await falso.GerarAsync(new PedidoTexto("s", "c", pedido, 100, "m"), default)).Texto, "falso");
        Assert.Equal(2, (await R(TextosRegras.PedidoRoteiro("contexto", Array.Empty<string>()))).GetProperty("falas").GetArrayLength());
        Assert.Equal(3, (await R(TextosRegras.PedidoGanchos)).GetProperty("ganchos").GetArrayLength());
        Assert.True((await R(TextosRegras.PedidoEncurtar(5))).TryGetProperty("texto", out _));
    }

    [Fact]
    public void Ssml_do_azure_escapa_o_texto_e_converte_a_velocidade()
    {
        var ssml = VozAzure.Ssml(new PedidoVoz("Tom & Jerry <3", "pt-BR-FranciscaNeural", 1.2m, "pt-BR", null));
        Assert.Contains("Tom &amp; Jerry &lt;3", ssml);
        Assert.Contains("rate='+20%'", ssml);
        Assert.Contains("rate='-50%'", VozAzure.Ssml(new PedidoVoz("a", "v", 0.1m, "pt-BR", null)));
    }

    [Fact]
    public void Provedor_escolhido_traduz_as_vozes_do_prototipo()
    {
        var p = new ProvedorEscolhido("openai", OrigensIa.Zippy, "k", null, null,
            JsonDocument.Parse("""{"tts":"gpt-4o-mini-tts","vozes":{"fem":"nova"}}""").RootElement.Clone(),
            JsonDocument.Parse("""{"voz_por_1000_caracteres":0.015}""").RootElement.Clone(), AoFalharIa.UsarCota);
        Assert.Equal("nova", p.VozDoProvedor("fem"));
        Assert.Equal("doc", p.VozDoProvedor("doc"));
        Assert.Equal("gpt-4o-mini-tts", p.ModeloDe("tts"));
        Assert.Equal(0.015m, p.Preco("voz_por_1000_caracteres"));
        Assert.Equal(0m, p.Preco("nao_tem"));
    }

    [Fact]
    public async Task ElevenLabs_devolve_audio_e_tempo_por_palavra()
    {
        var audio = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var corpo = JsonSerializer.Serialize(new
        {
            audio_base64 = audio,
            alignment = new
            {
                characters = new[] { "O", "i", " ", "j", "á" },
                character_start_times_seconds = new[] { 0.0, 0.1, 0.2, 0.3, 0.4 },
                character_end_times_seconds = new[] { 0.1, 0.2, 0.3, 0.4, 0.5 },
            },
        });
        var http = new Http(HttpStatusCode.OK, corpo);
        var voz = new VozElevenLabs(new HttpClient(http), "chave-teste", null);
        var r = await voz.SintetizarAsync(new PedidoVoz("Oi já", "voz1", 5m, "pt-BR", null), default);
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Bytes);
        Assert.Equal(new[] { "Oi", "já" }, r.Palavras!.Select(p => p.Texto));
        Assert.Equal(0.5m, r.DuracaoS);
        Assert.Contains("/v1/text-to-speech/voz1/with-timestamps", http.Ultimo!.RequestUri!.ToString());
        Assert.Equal("chave-teste", http.Ultimo.Headers.GetValues("xi-api-key").Single());
        Assert.Contains("\"speed\":1.2", http.UltimoCorpo); // limitado à faixa da ElevenLabs
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", TipoFalhaIa.ChaveRecusada)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"detail\":{\"status\":\"quota_exceeded\"}}", TipoFalhaIa.SemSaldo)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", TipoFalhaIa.Limite)]
    [InlineData(HttpStatusCode.BadGateway, "{}", TipoFalhaIa.ForaDoAr)]
    [InlineData(HttpStatusCode.BadRequest, "{}", TipoFalhaIa.Pedido)]
    public async Task Erros_do_provedor_viram_o_tipo_de_falha_certo(HttpStatusCode status, string corpo, TipoFalhaIa tipo)
    {
        var voz = new VozOpenAI(new HttpClient(new Http(status, corpo)), "k", null);
        var f = await Assert.ThrowsAsync<FalhaIaException>(() => voz.SintetizarAsync(new PedidoVoz("oi", "nova", 1, "pt-BR", null), default));
        Assert.Equal(tipo, f.Tipo);
        Assert.Equal(tipo != TipoFalhaIa.Pedido, f.TrocarResolve);
    }

    [Fact]
    public void Erros_de_ia_tem_codigo_e_status_para_a_tela()
    {
        var semCredito = IaErros.SemCreditos(5, 2);
        Assert.Equal(402, semCredito.Status);
        Assert.Contains("\"falta\":3", JsonSerializer.Serialize(semCredito.Detalhes));
        Assert.Equal(402, IaErros.ContaEmAtraso().Status);
        Assert.Equal(429, IaErros.LimiteMinuto().Status);
        Assert.Equal(502, IaErros.ChavePropriaFalhou("openai", TipoFalhaIa.ChaveRecusada).Status);
        Assert.Equal(422, IaErros.ProvedorFalhou("openai", TipoFalhaIa.Pedido).Status);
        Assert.Equal(503, IaErros.ProvedorFalhou("openai", TipoFalhaIa.ForaDoAr).Status);
    }

    private sealed class Relogio : TimeProvider
    {
        private DateTimeOffset _agora = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _agora;
        public void Avancar(TimeSpan t) => _agora += t;
    }

    private sealed class Http(HttpStatusCode status, string corpo) : HttpMessageHandler
    {
        public HttpRequestMessage? Ultimo { get; private set; }
        public string UltimoCorpo { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Ultimo = request;
            UltimoCorpo = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        }
    }
}
