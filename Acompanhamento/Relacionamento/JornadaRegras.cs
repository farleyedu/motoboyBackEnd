using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using APIBack.Acompanhamento.Fases;

namespace APIBack.Acompanhamento.Relacionamento
{
    public static class CategoriasJornada
    {
        public const string Fase = "fase", Decisao = "decisao", Proposta = "proposta", AtividadePlanejada = "atividade_planejada",
            AtividadeExecutada = "atividade_executada", Contato = "contato", Sinalizacao = "sinalizacao", Sistema = "sistema";
    }

    /// <summary>De onde veio a linha da jornada.</summary>
    public static class OrigensJornada
    {
        public const string Historico = "historico", Evento = "evento", Contato = "contato", Passagem = "passagem";
    }

    /// <summary>Uma linha crua da trilha: historico de fase/faixa, evento do relacionamento, marco de contato ou passagem sem mudanca de fase.</summary>
    public sealed class JornadaLinha
    {
        public string Id { get; set; } = string.Empty;
        public DateTimeOffset Em { get; set; }
        public string Origem { get; set; } = string.Empty;
        public string Tipo { get; set; } = string.Empty;
        public int? UsuarioId { get; set; }
        public string? UsuarioNome { get; set; }
        public string? De { get; set; }
        public string? Para { get; set; }
        public string? Motivo { get; set; }
        public string? Nome { get; set; }
        public string? Referencia { get; set; }
        public string? Detalhe { get; set; }
    }

    public sealed record JornadaAutorDto(int? Id, string Nome);

    public sealed record JornadaReferenciaDto(string Tipo, string Id);

    public sealed record JornadaItemDto(
        string Id, DateTimeOffset Em, string Categoria, string Titulo, string? Detalhe, string? Resultado, JornadaAutorDto Autor, JornadaReferenciaDto? Referencia);

    /// <summary>GET /lojas/{id}/jornada: da mais nova para a mais antiga. <c>ProximoCursor</c> nulo = acabou.</summary>
    public sealed record JornadaDto(Guid AcompanhamentoId, IReadOnlyList<JornadaItemDto> Itens, string? ProximoCursor);

    /// <summary>Monta a linha unica da jornada. Sem banco: a mesma leitura vale para eventos antigos (PascalCase, marcou/desmarcou).</summary>
    public static class JornadaRegras
    {
        public const int LimitePadrao = 50;
        public const int LimiteMaximo = 200;

        public static int Limite(int? pedido) => pedido is > 0 ? Math.Min(pedido.Value, LimiteMaximo) : LimitePadrao;

        public static string Cursor(JornadaLinha l) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(l.Em.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + l.Id));

        public static (DateTimeOffset Em, string Id)? LerCursor(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return null;
            try
            {
                var partes = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
                if (partes.Length == 2 && DateTimeOffset.TryParse(partes[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var em))
                    return (em, partes[1]);
            }
            catch (FormatException)
            {
            }
            throw new RelacionamentoException(422, "CURSOR_INVALIDO", "Cursor da jornada invalido.");
        }

        public static JornadaItemDto Item(JornadaLinha l)
        {
            var autor = new JornadaAutorDto(l.UsuarioId, l.UsuarioId.HasValue ? l.UsuarioNome ?? "Equipe" : "Sistema");
            return l.Origem switch
            {
                OrigensJornada.Historico => Historico(l, autor),
                OrigensJornada.Contato => new JornadaItemDto(l.Id, l.Em, CategoriasJornada.Contato,
                    l.Tipo == DirecoesContato.Recebida ? "Mensagem recebida" : "Mensagem enviada", null, null, autor,
                    l.Referencia == null ? null : new JornadaReferenciaDto("mensagem", l.Referencia)),
                OrigensJornada.Passagem => new JornadaItemDto(l.Id, l.Em, CategoriasJornada.Decisao, l.Nome ?? "Passagem", l.Motivo,
                    l.Tipo == StatusPassagem.PendenteAprovacao ? "pendente de aprovação" : null, autor,
                    l.Referencia == null ? null : new JornadaReferenciaDto("passagem", l.Referencia)),
                _ => Evento(l, autor)
            };
        }

        private static JornadaItemDto Historico(JornadaLinha l, JornadaAutorDto autor)
        {
            if (l.Tipo == "faixa")
                return new JornadaItemDto(l.Id, l.Em, CategoriasJornada.Sistema, $"Saúde: {NomeFaixa(l.De)} → {NomeFaixa(l.Para)}", l.Motivo, null, autor, null);
            var titulo = l.De == null
                ? $"Início em {FasesAcompanhamento.Nome(l.Para ?? string.Empty)}"
                : l.Nome ?? $"{FasesAcompanhamento.Nome(l.De)} → {FasesAcompanhamento.Nome(l.Para ?? string.Empty)}";
            return new JornadaItemDto(l.Id, l.Em, CategoriasJornada.Fase, titulo, l.Motivo, l.Para, autor,
                l.Referencia == null ? null : new JornadaReferenciaDto("passagem", l.Referencia));
        }

        private static string NomeFaixa(string? faixa) => faixa switch
        {
            FaixasAcompanhamento.Bem => "bem",
            FaixasAcompanhamento.Atencao => "atenção",
            FaixasAcompanhamento.PrecisaDeAjuda => "precisa de ajuda",
            _ => faixa ?? "—"
        };

        private static JornadaItemDto Evento(JornadaLinha l, JornadaAutorDto autor)
        {
            var d = LerDetalhe(l.Detalhe);
            string? T(string campo) => d.TryGetValue(campo, out var v) ? v : null;
            JornadaReferenciaDto? Ref(string tipo, string? id) => id == null ? null : new JornadaReferenciaDto(tipo, id);
            JornadaItemDto Item(string categoria, string titulo, string? detalhe = null, string? resultado = null, JornadaReferenciaDto? referencia = null) =>
                new(l.Id, l.Em, categoria, titulo, detalhe, resultado, autor, referencia);

            var versao = T("versao");
            var tipoPasso = T("tipo");
            switch (RelacionamentoRegras.TipoEvento(l.Tipo))
            {
                case "lead_criado": return Item(CategoriasJornada.Sistema, "Lead criado", T("origem"));
                case "passou": return Item(CategoriasJornada.Decisao, "Loja passada para outra pessoa", T("motivo"));
                case "assumiu": return Item(CategoriasJornada.Decisao, "Assumiu a loja", T("motivo"));
                case "cobrou_responsavel": return Item(CategoriasJornada.Decisao, "Responsável cobrado", T("mensagem"));
                case "retencao_registrada": return Item(CategoriasJornada.Decisao, "Retenção registrada", T("oferta"), T("resultado"), Ref("retencao", T("retencaoId")));
                case "saida_registrada": return Item(CategoriasJornada.Decisao, "Saída registrada", T("saidaEm"), T("resultado"), Ref("retencao", T("retencaoId")));

                case "marcada": return Item(CategoriasJornada.Sinalizacao, $"Marcada: {NomeMarcacao(T("tipo"))}", T("motivo"), T("categoria"));
                case "desmarcada": return Item(CategoriasJornada.Sinalizacao, $"Marcação tirada: {NomeMarcacao(T("tipo"))}", T("resolucao") ?? T("motivo"));
                case "marcacao_convertida": return Item(CategoriasJornada.Sistema, "Marcação convertida para precisa de ajuda");

                case "proximo_passo": return Item(CategoriasJornada.AtividadePlanejada, $"Agendou {tipoPasso}", T("nota"), T("data"), Ref("passo", T("passoId")));
                case "atividade_concluida":
                    return Item(CategoriasJornada.AtividadeExecutada, $"Concluiu {tipoPasso}", T("nota"), T("resultado"), Ref("passo", T("passoId")));
                case "atividade_reagendada":
                    return Item(CategoriasJornada.AtividadePlanejada, $"Reagendou {tipoPasso}", T("motivo"), T("para"), Ref("passo", T("novoPassoId")));
                case "atividade_cancelada": return Item(CategoriasJornada.AtividadePlanejada, $"Cancelou {tipoPasso}", T("motivo"), null, Ref("passo", T("passoId")));

                case "proposta_rascunho": return Item(CategoriasJornada.Proposta, $"Rascunho da proposta v{versao}");
                case "proposta_enviada":
                    return Item(CategoriasJornada.Proposta, $"Proposta v{versao} enviada", T("canal") == CanaisProposta.NaoInformado ? "Canal não informado (CRM antigo)" : $"{T("canal")}: {T("destinatario")}", T("valorMensal"));
                case "proposta_resposta": return Item(CategoriasJornada.Proposta, $"Resposta da proposta v{versao}", T("nota"), T("resposta"));
                case "proposta_aceita":
                    return Item(CategoriasJornada.Proposta, $"Proposta v{versao} aceita", T("aceitaAposValidade") == "true" ? "Aceita depois da validade" : T("aceite"), T("valorMensal"));
                case "proposta_guardada": return Item(CategoriasJornada.Proposta, "Proposta guardada", T("motivo"));
                default: return Item(CategoriasJornada.Sistema, l.Tipo.Replace('_', ' '));
            }
        }

        private static string NomeMarcacao(string? tipo) => tipo switch
        {
            TiposMarcacao.Atencao => "atenção",
            TiposMarcacao.PrecisaDeAjuda => "precisa de ajuda",
            TiposMarcacao.RiscoDeSaida => "risco de saída",
            TiposMarcacao.NaoIncomodar => "não incomodar",
            _ => tipo ?? "—"
        };

        /// <summary>Campos de primeiro nivel como texto, com a chave em camelCase (eventos antigos gravaram PascalCase).</summary>
        private static IReadOnlyDictionary<string, string> LerDetalhe(string? json)
        {
            var campos = new Dictionary<string, string>(StringComparer.Ordinal);
            if (RelacionamentoRegras.DetalheEmCamelCase(json) is not { ValueKind: JsonValueKind.Object } raiz) return campos;
            foreach (var p in raiz.EnumerateObject())
            {
                var texto = p.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => p.Value.GetRawText()
                };
                if (!string.IsNullOrWhiteSpace(texto)) campos[p.Name] = texto;
            }
            return campos;
        }
    }
}
