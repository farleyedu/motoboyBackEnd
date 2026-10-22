using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.Service
{
    public sealed record ProntidaoItemDto(
        string Codigo, string Grupo, string Titulo, string Status, bool Bloqueia, string Mensagem,
        string ComoResolver, string QuemResolve, CorrecaoDto Correcao);

    /// <summary>
    /// pendenciasBloqueantes e a QUANTIDADE de itens obrigatorios que faltam. percentual = itens obrigatorios ok / itens obrigatorios
    /// que se aplicam (a loja chega a 100% exatamente quando podeComecarAOperar fica verdadeiro).
    /// diasTesteRestantes so existe em situacao "teste"; negativo = vencido (nada bloqueia sozinho).
    /// </summary>
    public sealed record ProntidaoDto(
        string Situacao, int Percentual, bool PodeComecarAOperar, int PendenciasBloqueantes,
        DateTimeOffset? TesteTerminaEm, int? DiasTesteRestantes, IReadOnlyList<ProntidaoItemDto> Itens);

    /// <summary>Resumo de uma loja para o painel da Gestao.</summary>
    public sealed record ProntidaoResumoDto(int Percentual, bool PodeComecarAOperar, int PendenciasBloqueantes, IReadOnlyList<string> Principais);

    /// <summary>Estado de um numero de WhatsApp da loja (so leitura, vem de canal_whatsapp).</summary>
    public sealed record CanalEstadoLeitura(
        string Status, string Origem, string? WebhookEstado, DateTime? UltimoRecebimentoEm, DateTime? UltimoEnvioOkEm);

    /// <summary>Tudo o que a prontidao precisa saber da loja, ja lido do banco e da configuracao.</summary>
    public sealed class ProntidaoDados
    {
        public string Situacao { get; set; } = SituacoesComerciais.Implantacao;
        public DateTimeOffset? TesteTerminaEm { get; set; }
        public Guid? TipoEstabelecimentoId { get; set; }
        public string? PlanoComercial { get; set; }
        public IReadOnlyCollection<string> Modulos { get; set; } = Array.Empty<string>();
        public int ResponsaveisAtivos { get; set; }
        public int ResponsaveisComSenha { get; set; }
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? Cep { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public int DiasAbertos { get; set; }
        public decimal? RaioEntregaKm { get; set; }
        public int ProdutosVendaveis { get; set; }
        public bool CardapioWebConfigurado { get; set; }
        public bool CardapioWebPublicado { get; set; }
        public IReadOnlyList<CanalEstadoLeitura> Canais { get; set; } = Array.Empty<CanalEstadoLeitura>();
        // Avisos.
        public decimal? TaxaEntregaFixa { get; set; }
        public decimal? TaxaEntregaPorKm { get; set; }
        public int ZonasAtivas { get; set; }
        public int? TempoPreparoMin { get; set; }
        public decimal? PedidoMinimo { get; set; }
        public int Categorias { get; set; }
        public int ProdutosComImagem { get; set; }
        public int ProdutosTotal { get; set; }
        public string? NomePublico { get; set; }
        public string? UrlLogo { get; set; }
        public string? CorPrimaria { get; set; }
        public string? Telefone { get; set; }
        public string? Email { get; set; }
        public string? Cnpj { get; set; }
        public bool IfoodAutorizado { get; set; }
        public bool ImpressaoOnline { get; set; }
        public int Atendentes { get; set; }
        public int MotoboysVinculados { get; set; }
        public int MotoboysComPlano { get; set; }
        public bool RemuneracaoPadraoDefinida { get; set; }
        // Infraestrutura (variaveis do servidor).
        public bool InfraDeliveryTracking { get; set; }
        public bool InfraMapsApiKey { get; set; }
        public bool InfraChaveToken { get; set; }
        public bool InfraCardapioBaseUrl { get; set; }
        public bool InfraMetaAppSecret { get; set; }
        public bool InfraMetaVerifyToken { get; set; }
    }

    /// <summary>
    /// A UNICA lugar onde se decide o que bloqueia o inicio da operacao e o que e so recomendacao. Para mudar uma severidade,
    /// edite <see cref="Bloqueantes"/>; os textos de cada item ficam em <see cref="Avaliar"/>.
    /// </summary>
    public static class ProntidaoRegras
    {
        public const string TipoEstabelecimento = "tipo_estabelecimento";
        public const string PlanoModulos = "plano_modulos";
        public const string GerenteSenha = "gerente_senha";
        public const string Endereco = "endereco";
        public const string Coordenada = "coordenada";
        public const string HorarioSemanal = "horario_semanal";
        public const string RaioEntrega = "raio_entrega";
        public const string ProdutoVendavel = "produto_vendavel";
        public const string CardapioWeb = "cardapioweb_publicado";
        public const string WhatsappRecebendo = "whatsapp_recebendo";
        public const string Infra = "infra";

        /// <summary>Itens obrigatorios para "Comecar a operar" (os de infra sao sempre obrigatorios).</summary>
        public static readonly IReadOnlySet<string> Bloqueantes = new HashSet<string>(StringComparer.Ordinal)
        {
            TipoEstabelecimento, PlanoModulos, GerenteSenha, Endereco, Coordenada, HorarioSemanal, RaioEntrega, ProdutoVendavel,
            CardapioWeb, WhatsappRecebendo, Infra
        };

        public const string Ok = "ok";
        public const string Pendente = "pendente";
        public const string Aviso = "aviso";
        public const string NaoSeAplica = "nao_se_aplica";

        public static bool TemModulo(ProntidaoDados d, string modulo) =>
            d.Modulos.Any(m => string.Equals(m, modulo, StringComparison.OrdinalIgnoreCase));

        public static bool Preenchido(string? valor) => !string.IsNullOrWhiteSpace(valor);

        /// <summary>Coordenada valida: nao nula, finita, dentro do globo e diferente de 0/0.</summary>
        public static bool CoordenadaValida(double? latitude, double? longitude) =>
            latitude.HasValue && longitude.HasValue && double.IsFinite(latitude.Value) && double.IsFinite(longitude.Value)
            && Math.Abs(latitude.Value) <= 90 && Math.Abs(longitude.Value) <= 180
            && !(latitude.Value == 0 && longitude.Value == 0);

        public static bool UfValida(string? uf) => uf is { Length: 2 } && uf.All(char.IsLetter);

        public static bool CepValido(string? cep) => cep != null && cep.Count(char.IsDigit) == 8;

        public static bool EnderecoCompleto(ProntidaoDados d) =>
            Preenchido(d.Logradouro) && Preenchido(d.Numero) && Preenchido(d.Bairro) && Preenchido(d.Cidade)
            && UfValida(d.Uf?.Trim()) && CepValido(d.Cep);

        /// <summary>
        /// UNICO ponto que le o estado do WhatsApp para a prontidao. Recebendo = existe numero da loja que nao esta inativo/com erro e
        /// que recebeu mensagem de verdade (webhook "recebendo", ultimo_recebimento_em preenchido ou, nos numeros da Zippy/central,
        /// um envio que deu certo). Para mudar o criterio, troque so este metodo.
        /// </summary>
        public static bool WhatsappEstaRecebendo(IReadOnlyList<CanalEstadoLeitura> canais) =>
            canais.Any(c => !string.Equals(c.Status, "inativo", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(c.Status, "erro", StringComparison.OrdinalIgnoreCase)
                            && (string.Equals(c.WebhookEstado, "recebendo", StringComparison.OrdinalIgnoreCase)
                                || c.UltimoRecebimentoEm.HasValue
                                || (c.Origem is "zippy" or "central" && c.UltimoEnvioOkEm.HasValue)));

        public static ProntidaoDto Avaliar(ProntidaoDados d, bool visaoGestao, DateTimeOffset agora)
        {
            var itens = new List<ProntidaoItemDto>();
            var cardapioWeb = TemModulo(d, "CARDAPIOWEB");
            var delivery = TemModulo(d, "DELIVERY");
            var whatsappRecebendo = WhatsappEstaRecebendo(d.Canais);

            // ---- Obrigatorios ----
            Add(itens, TipoEstabelecimento, "negocio", "Tipo de estabelecimento", d.TipoEstabelecimentoId.HasValue && d.TipoEstabelecimentoId != Guid.Empty,
                "O tipo de estabelecimento está definido.", "O tipo de estabelecimento (restaurante, loja…) ainda não foi definido.",
                "Peça à Zippy para definir o tipo de estabelecimento da loja.", QuemResolve.Zippy, new CorrecaoDto());
            Add(itens, PlanoModulos, "negocio", "Plano e módulos", d.Modulos.Count > 0 && (Preenchido(d.PlanoComercial) || d.Situacao == SituacoesComerciais.Ativo),
                "O plano e os módulos da loja estão definidos.", "O plano ou os módulos da loja ainda não foram definidos.",
                "Peça à Zippy para definir o plano e os módulos da loja.", QuemResolve.Zippy, new CorrecaoDto());
            Add(itens, GerenteSenha, "equipe", "Gerente com senha", d.ResponsaveisAtivos > 0 && d.ResponsaveisComSenha > 0,
                "O gerente já definiu a própria senha.",
                d.ResponsaveisAtivos == 0 ? "A loja ainda não tem um gerente cadastrado." : "O gerente ainda não definiu a senha de acesso.",
                "Em Equipe, gere o link de primeiro acesso do gerente e envie para ele definir a senha.",
                QuemResolve.Loja, CorrecaoDto.Config("equipe"));
            Add(itens, Endereco, "negocio", "Endereço completo", EnderecoCompleto(d),
                "O endereço da loja está completo.",
                "Faltam dados no endereço da loja (rua, número, bairro, cidade, UF e CEP).",
                "Preencha rua, número, bairro, cidade, UF e CEP em Configurações > Negócio > Endereço.",
                QuemResolve.Loja, CorrecaoDto.Config("negocio", "endereco"));
            Add(itens, Coordenada, "negocio", "Posição no mapa", CoordenadaValida(d.Latitude, d.Longitude),
                "A posição da loja no mapa está confirmada.", "A loja ainda não tem a posição no mapa (latitude e longitude).",
                "Complete o endereço e confirme o ponto da loja no mapa em Configurações > Negócio > Endereço.",
                QuemResolve.Loja, CorrecaoDto.Config("negocio", "endereco"));
            Add(itens, HorarioSemanal, "negocio", "Horário de funcionamento", d.DiasAbertos > 0,
                $"A loja abre em {d.DiasAbertos} dia(s) da semana.", "Nenhum dia da semana está aberto no horário de funcionamento.",
                "Marque ao menos um dia aberto, com hora de abrir e fechar, em Configurações > Negócio > Horários.",
                QuemResolve.Loja, CorrecaoDto.Config("negocio", "horarios"));
            Add(itens, RaioEntrega, "delivery", "Raio de entrega", d.RaioEntregaKm is > 0,
                $"A loja entrega até {d.RaioEntregaKm:0.##} km.", "O raio de entrega ainda não foi informado (precisa ser maior que zero).",
                "Informe o raio de entrega, em km, em Configurações > Delivery.",
                QuemResolve.Loja, CorrecaoDto.Config("delivery"));
            Add(itens, ProdutoVendavel, "cardapio", "Produto para vender", d.ProdutosVendaveis > 0,
                $"A loja tem {d.ProdutosVendaveis} produto(s) à venda.", "Nenhum produto ativo, disponível e com preço maior que zero.",
                "Cadastre ao menos um produto ativo, disponível e com preço no Cardápio.",
                QuemResolve.Loja, CorrecaoDto.Pagina("/apps/cardapio"));
            Add(itens, CardapioWeb, "cardapio", "Cardápio web publicado", d.CardapioWebConfigurado && d.CardapioWebPublicado,
                "O cardápio web está configurado e publicado.",
                d.CardapioWebConfigurado ? "O cardápio web está configurado, mas ainda não foi publicado." : "O cardápio web ainda não foi configurado.",
                "Configure e publique o cardápio web.", QuemResolve.Loja, CorrecaoDto.Pagina("/apps/cardapioweb"),
                naoSeAplica: !cardapioWeb);
            // O WhatsApp so impede o inicio quando o cardapio web esta ativo (e por ele que o cliente confirma o pedido).
            Add(itens, WhatsappRecebendo, "atendimento", "WhatsApp recebendo mensagens", whatsappRecebendo,
                "O WhatsApp da loja está recebendo mensagens.",
                d.Canais.Count == 0 ? "A loja ainda não tem um número de WhatsApp conectado." : "O WhatsApp da loja ainda não recebeu nenhuma mensagem.",
                "Conecte o número de WhatsApp e mande uma mensagem de teste em Configurações > Atendimento > Números.",
                QuemResolve.Loja, CorrecaoDto.Config("atendimento", "numeros"), bloqueia: cardapioWeb);

            // ---- Infraestrutura ----
            AddInfra(itens, d, cardapioWeb || TemModulo(d, "WHATSAPP"), visaoGestao);

            // ---- Recomendados ----
            Add(itens, "taxa_entrega", "delivery", "Taxa ou zonas de entrega",
                (d.TaxaEntregaFixa ?? 0) > 0 || (d.TaxaEntregaPorKm ?? 0) > 0 || d.ZonasAtivas > 0,
                "A taxa de entrega está configurada.", "Sem taxa nem zonas de entrega: o frete sai grátis para todos os clientes.",
                "Defina a taxa de entrega ou as zonas por distância em Configurações > Delivery.",
                QuemResolve.Loja, CorrecaoDto.Config("delivery"), naoSeAplica: !delivery);
            Add(itens, "tempo_preparo", "delivery", "Tempo de preparo", (d.TempoPreparoMin ?? 0) > 0,
                "O tempo de preparo está definido.", "O tempo de preparo não foi informado.",
                "Informe o tempo médio de preparo em Configurações > Delivery.", QuemResolve.Loja, CorrecaoDto.Config("delivery"));
            Add(itens, "pedido_minimo", "delivery", "Pedido mínimo", (d.PedidoMinimo ?? 0) > 0,
                "O pedido mínimo está definido.", "Sem pedido mínimo: a loja aceita pedidos de qualquer valor.",
                "Se quiser, defina o pedido mínimo em Configurações > Delivery.", QuemResolve.Loja, CorrecaoDto.Config("delivery"));
            Add(itens, "categorias_imagens", "cardapio", "Categorias e imagens",
                d.Categorias > 0 && d.ProdutosComImagem > 0,
                "O cardápio tem categorias e produtos com foto.",
                d.Categorias == 0 ? "O cardápio ainda não tem categorias." : "Nenhum produto tem foto.",
                "Organize os produtos em categorias e coloque fotos no Cardápio.", QuemResolve.Loja, CorrecaoDto.Pagina("/apps/cardapio"));
            Add(itens, "nome_publico", "cardapio", "Nome público do cardápio web", Preenchido(d.NomePublico),
                "O nome público do cardápio web está definido.", "O cardápio web ainda não tem nome público.",
                "Defina o nome público no cardápio web.", QuemResolve.Loja, CorrecaoDto.Pagina("/apps/cardapioweb"), naoSeAplica: !cardapioWeb);
            Add(itens, "marca", "negocio", "Logo e cores", Preenchido(d.UrlLogo) && Preenchido(d.CorPrimaria),
                "A logo e as cores da loja estão definidas.", "Falta a logo ou a cor principal da loja.",
                "Envie a logo e escolha as cores em Configurações > Negócio.", QuemResolve.Loja, CorrecaoDto.Config("negocio"));
            Add(itens, "contato", "negocio", "Telefone e e-mail", Preenchido(d.Telefone) && Preenchido(d.Email),
                "O telefone e o e-mail da loja estão preenchidos.", "Falta o telefone ou o e-mail da loja.",
                "Preencha o telefone e o e-mail em Configurações > Negócio.", QuemResolve.Loja, CorrecaoDto.Config("negocio"));
            Add(itens, "cnpj", "negocio", "CNPJ", Preenchido(d.Cnpj),
                "O CNPJ da loja está cadastrado.", "O CNPJ da loja ainda não foi cadastrado.",
                "O CNPJ é cadastrado pela Zippy: envie o número para a equipe.", QuemResolve.Zippy, new CorrecaoDto());
            Add(itens, "ifood", "integracoes", "iFood conectado", d.IfoodAutorizado,
                "A loja está conectada ao iFood.", "A conexão com o iFood ainda não foi autorizada.",
                "Autorize a conexão com o iFood em Configurações > iFood.", QuemResolve.Loja, CorrecaoDto.Config("ifood"),
                naoSeAplica: !TemModulo(d, "IFOOD"));
            Add(itens, "impressao", "integracoes", "Impressora pareada", d.ImpressaoOnline,
                "Há uma estação de impressão online.", "Nenhuma estação de impressão está online.",
                "Pareie e abra a estação de impressão em Configurações > Impressão.", QuemResolve.Loja, CorrecaoDto.Config("impressao"),
                naoSeAplica: !TemModulo(d, "IMPRESSAO"));
            Add(itens, "equipe", "equipe", "Atendentes", d.Atendentes > 0,
                "A loja tem atendentes cadastrados.", "A loja ainda não tem atendentes.",
                "Convide os atendentes em Configurações > Equipe.", QuemResolve.Loja, CorrecaoDto.Config("equipe"));
            Add(itens, "motoboys", "delivery", "Motoboys vinculados", d.MotoboysVinculados > 0,
                "A loja tem motoboys vinculados.", "Nenhum motoboy está vinculado à loja.",
                "Vincule os motoboys em Configurações > Equipe.", QuemResolve.Loja, CorrecaoDto.Config("equipe"),
                naoSeAplica: !delivery);
            var remuneracaoOk = d.RemuneracaoPadraoDefinida || (d.MotoboysVinculados > 0 && d.MotoboysComPlano >= d.MotoboysVinculados);
            Add(itens, "remuneracao_padrao", "delivery", "Remuneração dos motoboys", remuneracaoOk,
                "A remuneração dos motoboys está definida.", "Defina quanto você paga aos motoboys.",
                "Defina a remuneração padrão em Configurações > Remuneração; vale para todo motoboy novo.",
                QuemResolve.Loja, CorrecaoDto.Config("remuneracao"), naoSeAplica: !(delivery && TemModulo(d, "REMUNERACAO")));

            var aplicaveis = itens.Where(i => i.Bloqueia && i.Status != NaoSeAplica).ToList();
            var pendencias = aplicaveis.Count(i => i.Status == Pendente);
            var percentual = aplicaveis.Count == 0 ? 100 : (int)Math.Round(100.0 * (aplicaveis.Count - pendencias) / aplicaveis.Count);
            DateTimeOffset? fim = d.Situacao == SituacoesComerciais.Teste ? d.TesteTerminaEm : null;
            return new ProntidaoDto(d.Situacao, percentual, pendencias == 0, pendencias, fim, DiasRestantes(fim, agora), itens);
        }

        /// <summary>Dias que faltam para o fim do teste (negativo = vencido ha N dias). Nulo fora do teste.</summary>
        public static int? DiasRestantes(DateTimeOffset? fim, DateTimeOffset agora)
        {
            if (!fim.HasValue) return null;
            var diferenca = fim.Value - agora;
            return diferenca >= TimeSpan.Zero ? (int)Math.Ceiling(diferenca.TotalDays) : -(int)Math.Ceiling(-diferenca.TotalDays);
        }

        public static ProntidaoResumoDto Resumir(ProntidaoDto prontidao) =>
            new(prontidao.Percentual, prontidao.PodeComecarAOperar, prontidao.PendenciasBloqueantes,
                prontidao.Itens.Where(i => i.Bloqueia && i.Status == Pendente).Take(3).Select(i => i.Titulo).ToList());

        /// <summary>As pendencias obrigatorias, em formato de erro (para 422 e 403 com a lista e o caminho).</summary>
        public static IReadOnlyList<PendenciaErroDto> Pendencias(ProntidaoDto prontidao, int? max = null)
        {
            var lista = prontidao.Itens.Where(i => i.Bloqueia && i.Status == Pendente)
                .Select(i => new PendenciaErroDto(i.Codigo, i.Titulo, i.Mensagem, i.ComoResolver, i.QuemResolve, i.Correcao));
            return (max.HasValue ? lista.Take(max.Value) : lista).ToList();
        }

        private static void Add(List<ProntidaoItemDto> itens, string codigo, string grupo, string titulo, bool ok,
            string mensagemOk, string mensagemPendente, string comoResolver, string quem, CorrecaoDto correcao,
            bool naoSeAplica = false, bool? bloqueia = null)
        {
            var obrigatorio = bloqueia ?? Bloqueantes.Contains(codigo);
            var status = naoSeAplica ? NaoSeAplica : ok ? Ok : obrigatorio ? Pendente : Aviso;
            itens.Add(new ProntidaoItemDto(codigo, grupo, titulo, status, obrigatorio && !naoSeAplica,
                ok ? mensagemOk : mensagemPendente, ok || naoSeAplica ? string.Empty : comoResolver, quem, correcao));
        }

        private static void AddInfra(List<ProntidaoItemDto> itens, ProntidaoDados d, bool usaWhatsapp, bool visaoGestao)
        {
            var verificacoes = new (string Codigo, string Titulo, bool Ok, bool Aplica, string Variavel)[]
            {
                ("infra_delivery_tracking", "Rastreamento do delivery", d.InfraDeliveryTracking, true, "DeliveryTracking__Enabled"),
                ("infra_google_maps", "Chave do Google Maps", d.InfraMapsApiKey, true, "Google__MapsApiKey"),
                ("infra_chave_token", "Chave de cifra dos tokens do WhatsApp", d.InfraChaveToken, usaWhatsapp, "Atendimento__ChaveToken"),
                ("infra_cardapio_base_url", "Endereço público do cardápio", d.InfraCardapioBaseUrl, usaWhatsapp, "Atendimento__CardapioBaseUrl"),
                ("infra_meta_app_secret", "App Secret da Meta", d.InfraMetaAppSecret, usaWhatsapp, "Automation__Meta__AppSecret"),
                ("infra_meta_verify_token", "Token de verificação da Meta", d.InfraMetaVerifyToken, usaWhatsapp, "Automation__VerifyToken"),
            };
            if (visaoGestao)
            {
                foreach (var v in verificacoes)
                    Add(itens, v.Codigo, "infra", v.Titulo, v.Ok, "Configurado no servidor.", $"Falta configurar {v.Variavel} no servidor.",
                        $"Defina a variável de ambiente {v.Variavel} no servidor e reinicie a API.", QuemResolve.Infra, new CorrecaoDto(),
                        naoSeAplica: !v.Aplica, bloqueia: true);
                return;
            }

            // O cliente nao ve variavel de servidor: so "Aguardando a Zippy".
            var faltando = verificacoes.Any(v => v.Aplica && !v.Ok);
            Add(itens, Infra, "infra", "Aguardando a Zippy", !faltando, "O ambiente da Zippy está pronto.",
                "A Zippy ainda está preparando o ambiente da sua loja.", "Nada a fazer aqui: a Zippy já foi avisada e conclui esta etapa.",
                QuemResolve.Infra, new CorrecaoDto(), bloqueia: true);
        }
    }
}
