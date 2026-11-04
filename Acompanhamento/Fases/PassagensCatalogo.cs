using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Payments.Assinaturas;
using APIBack.Service;
using F = APIBack.Acompanhamento.Fases.FasesAcompanhamento;

namespace APIBack.Acompanhamento.Fases
{
    /// <summary>O que a passagem faz no servidor (ver <see cref="AcompanhamentoService"/> e o store).</summary>
    public enum EfeitoPassagem
    {
        EnviarProposta,
        VoltarAConversar,
        FecharNaConversa,
        PropostaAceita,
        TesteSemCompromisso,
        ComecarTeste,
        ProrrogarTeste,
        AtivarPlano,
        PediuParaCancelar,
        Ficou,
        Encerrar,
        PararLoja,
        DeclararPausa,
        VoltarDaPausa,
        Reimplantar,
        VoltarAOperar,
        Perder,
        Retomar,
        QuerVoltar
    }

    /// <summary>Um requisito da passagem. <see cref="Pendencia"/> devolve nulo quando esta pronto, ou o que falta.</summary>
    public sealed record RequisitoDef(string Chave, string Descricao, Func<ContextoPassagem, string?> Pendencia);

    /// <summary><paramref name="Caminho"/>: o caminho apontado quando falta requisito. Nulo = o nome da passagem.</summary>
    public sealed record PassagemDef(
        string De, string Para, string Nome, string Quem, IReadOnlyList<RequisitoDef> Requisitos,
        string OQueAcontece, string Volta, EfeitoPassagem Efeito, string? Caminho = null);

    /// <summary>Passagem que nao existe: o motivo, o caminho em texto e, quando ha, a sequencia de fases possivel.</summary>
    public sealed record RecusaPassagem(string Motivo, string Caminho, IReadOnlyList<string> Sequencia);

    /// <summary>
    /// As tabelas do item 2 (docs/crm-inteligente/01-backend-base-crm.md): passagens que existem e as que nao existem.
    /// O servidor valida tudo; a tela so mostra. O unico portao obrigatorio e o teste: ninguem chega a Operando sem ele.
    /// </summary>
    public static class PassagensCatalogo
    {
        /// <summary>Por que a proposta volta para a conversa. Renegociar nao volta: revisa a proposta, sem mudar a fase.</summary>
        public static readonly string[] Motivos = { "esfriou", "mudou_quem_decide" };
        public static readonly string[] Aceites = { "verbal", "whatsapp", "assinado" };
        public static readonly string[] QuandoSai = { "aviso_30_dias", "fim_do_ciclo", "agora" };
        public static readonly string[] MotivosReimplantar = { "troca_de_dono", "novo_cnpj", "recomeco" };
        public static readonly string[] TentarDeNovo = { "nao", "30", "90" };
        public static readonly int[] DiasProrrogacao = { 7, 15 };
        public static readonly string[] FormasPagamento = { "pix", "cartao", "boleto" };

        // Lazy: os requisitos sao campos estaticos declarados mais abaixo e precisam existir antes da montagem.
        private static readonly Lazy<IReadOnlyList<PassagemDef>> _todas = new(Montar);

        public static IReadOnlyList<PassagemDef> Todas => _todas.Value;

        public static PassagemDef? Obter(string de, string para) =>
            Todas.FirstOrDefault(p => p.De == de && p.Para == para);

        /// <summary>
        /// Recusa da passagem de -> para para esta loja, ou nulo se ela existe. Implantacao -> Operando so existe para quem ja passou
        /// pelo teste (volta da reimplantacao); Implantacao -> Teste, ao contrario, nao existe para quem ja passou.
        /// </summary>
        public static RecusaPassagem? Recusa(string de, string para, LojaFatos loja)
        {
            if (de == F.Implantacao && para == F.Operando && !loja.TesteConcluido)
                return new("Toda loja passa pelo teste antes de operar.", "Implantação → Teste → Operando",
                    new[] { F.Implantacao, F.Teste, F.Operando });
            if (de == F.Implantacao && para == F.Teste && loja.TesteConcluido)
                return new("A loja já passou pelo teste: a reimplantação não reinicia o teste.", "Implantação → Operando, ao fechar o checklist",
                    new[] { F.Implantacao, F.Operando });
            if (Obter(de, para) != null) return null;

            if (F.EmConquista(de) && para == F.Operando)
                return new("A loja precisa existir e passar pelo teste.", "→ Implantação → Teste → Operando",
                    new[] { de, F.Implantacao, F.Teste, F.Operando });
            if (de == F.Teste && para == F.Implantacao)
                return new("A loja continua configurável no teste.", "Prorrogar o teste", new[] { F.Teste, F.Teste });
            if (de == F.Teste && para == F.Saindo)
                return new("No teste não há contrato para cancelar.", "Perder", new[] { F.Teste, F.Perdido });
            if (de is F.Conversa or F.Proposta or F.Implantacao && para == F.Saindo)
                return new("Quem não é cliente não cancela.", "Perder", new[] { de, F.Perdido });
            if (de == F.Operando && para == F.Teste)
                return new("O teste é só na entrada.", "Liberar o módulo por alguns dias em Plano e módulos", Array.Empty<string>());
            if (de == F.Operando && F.EmConquista(para))
                return new("Cliente não volta para a conquista.", "Criar oportunidade de venda a mais", Array.Empty<string>());
            if (de == F.Implantacao && F.EmConquista(para))
                return new("A loja já foi criada.", "Perder e retomar depois", new[] { F.Implantacao, F.Perdido, F.Conversa });
            if (de == F.Teste && F.EmConquista(para))
                return new("A loja já está testando.", "Registrar a proposta durante o teste, sem mudar a fase", Array.Empty<string>());
            if (de == F.Saindo)
                return new("Saindo só vai para Operando (ficou) ou Encerrado.", "—", Array.Empty<string>());
            if (de == para)
                return new($"A loja já está em {F.Nome(de)}.", "—", Array.Empty<string>());

            var sequencia = Sequencia(de, para, loja);
            return new("Essa passagem não existe.",
                sequencia.Count > 0 ? string.Join(" → ", sequencia.Select(F.Nome)) : "—",
                sequencia);
        }

        /// <summary>Menor sequencia de passagens existentes de -> para (sem a prorrogacao, que nao muda de fase). Vazia se nao ha.</summary>
        public static IReadOnlyList<string> Sequencia(string de, string para, LojaFatos loja)
        {
            var anterior = new Dictionary<string, string> { [de] = de };
            var fila = new Queue<string>();
            fila.Enqueue(de);
            while (fila.Count > 0)
            {
                var atual = fila.Dequeue();
                if (atual == para) break;
                foreach (var p in Todas.Where(p => p.De == atual && p.Para != atual))
                {
                    var fatos = loja with { Fase = atual };
                    if (Recusa(atual, p.Para, fatos) != null || anterior.ContainsKey(p.Para)) continue;
                    anterior[p.Para] = atual;
                    fila.Enqueue(p.Para);
                }
            }

            if (!anterior.ContainsKey(para) || de == para) return Array.Empty<string>();
            var caminho = new List<string> { para };
            while (caminho[^1] != de) caminho.Add(anterior[caminho[^1]]);
            caminho.Reverse();
            return caminho;
        }

        private static IReadOnlyList<PassagemDef> Montar()
        {
            var list = new List<PassagemDef>
            {
                new(F.Conversa, F.Proposta, "Enviar a proposta", QuemDecide.Voce,
                    new[] { PropostaEnviada },
                    "Entra em Proposta só com o envio registrado; validade de 7 dias a partir do envio.", "Sim",
                    EfeitoPassagem.EnviarProposta, CaminhoRegistrarEnvio),
                new(F.Proposta, F.Conversa, "Voltar a conversar", QuemDecide.Voce,
                    new[] { MotivoDeVolta },
                    "A proposta fica guardada como versão.", "Sim", EfeitoPassagem.VoltarAConversar),
                new(F.Conversa, F.Implantacao, "Fechou na conversa, sem proposta formal", QuemDecide.Voce,
                    new[] { QuemDecideRequisito, Aceite, PlanoDoCatalogo, Documento, ValorCombinado },
                    "Cria empresa e loja em implantação, sem operar; cria o checklist; cobrança só depois do teste.",
                    "Se desistir, vira Perdido e a loja é desativada.", EfeitoPassagem.FecharNaConversa),
                new(F.Proposta, F.Implantacao, "Proposta aceita", QuemDecide.Voce,
                    new[] { PropostaRegistrada, ValorDaProposta, Aceite, Documento },
                    "Cria empresa e loja em implantação, com o valor da proposta aceita; cobrança só depois do teste.",
                    "Se desistir, vira Perdido e a loja é desativada.", EfeitoPassagem.PropostaAceita),
                new(F.Conversa, F.Teste, "Teste sem compromisso", QuemDecide.Voce, new[] { Documento },
                    "Cria a loja, que passa por Implantação; o teste começa quando a loja clicar em Começar a operar ou quando você passar para Teste.",
                    "Se desistir, vira Perdido e a loja é desativada.", EfeitoPassagem.TesteSemCompromisso),
                new(F.Proposta, F.Teste, "Teste sem compromisso", QuemDecide.Voce, new[] { Documento },
                    "Cria a loja, que passa por Implantação; o teste começa quando a loja clicar em Começar a operar ou quando você passar para Teste.",
                    "Se desistir, vira Perdido e a loja é desativada.", EfeitoPassagem.TesteSemCompromisso),
                new(F.Implantacao, F.Teste, "Começar o teste", QuemDecide.SozinhoOuVoce, new[] { LojaCriada, ChecklistSemTrava },
                    "15 dias com tudo liberado; a cobrança não começa.", "Não volta; prorrogue o teste.", EfeitoPassagem.ComecarTeste),
                new(F.Teste, F.Teste, "Prorrogar o teste", QuemDecide.Voce,
                    new[] { Texto("motivo", "Motivo", d => d.Motivo), DiasDeProrrogacao },
                    "Nova data; a cobrança é adiada junto; a segunda prorrogação fica pendente até o gestor aprovar.", "—",
                    EfeitoPassagem.ProrrogarTeste),
                new(F.Teste, F.Operando, "Ativar o plano agora", QuemDecide.Voce, new[] { PlanoDoCatalogo, FormaPagamento },
                    "Encerra o teste; primeira cobrança no dia seguinte; módulos do plano.", "Não", EfeitoPassagem.AtivarPlano),
                new(F.Operando, F.Saindo, "Pediu para cancelar", QuemDecide.Voce,
                    new[] { Texto("motivo", "Motivo", d => d.Motivo), Escolha("quando", "Quando sai (30 dias de aviso, fim do ciclo ou agora)", d => d.Quando, QuandoSai) },
                    "Continua operando e pagando até a data; fica em Precisa de ajuda até registrar a tentativa de retenção.",
                    "Sim, se ficar.", EfeitoPassagem.PediuParaCancelar),
                new(F.Saindo, F.Operando, "Ficou", QuemDecide.Voce,
                    new[] { Texto("oQueFez", "O que fez a loja ficar", d => d.OQueFez) },
                    "Cancela a saída; a oferta vale no próximo ciclo.", "—", EfeitoPassagem.Ficou),
                new(F.Saindo, F.Encerrado, "Encerrar", QuemDecide.Voce, new[] { SemCobrancaEmAberto },
                    "Bloqueia e para a cobrança; o histórico fica. A data de saída não encerra sozinha: registre aqui.", "Pode voltar.", EfeitoPassagem.Encerrar),
                new(F.Operando, F.Encerrado, "Parar a loja", QuemDecide.Voce,
                    new[] { Texto("motivo", "Motivo", d => d.Motivo), LojaAvisada },
                    "A equipe entra mas não acessa nada; cardápio e robô param; entregas na rua terminam; a cobrança para.",
                    "Desbloquear devolve tudo.", EfeitoPassagem.PararLoja),
                new(F.Operando, F.Pausa, "Declarar pausa", QuemDecide.Voce,
                    new[] { Texto("motivo", "Motivo", d => d.Motivo), DataDeVolta },
                    "Registra a pausa: a falta de uso não deixa a loja vermelha. A cobrança não muda.",
                    "Não volta sozinha: registre a volta na data.", EfeitoPassagem.DeclararPausa),
                new(F.Pausa, F.Operando, "Voltar da pausa", QuemDecide.Voce, Array.Empty<RequisitoDef>(),
                    "A loja volta a contar como operando.", "—", EfeitoPassagem.VoltarDaPausa),
                new(F.Operando, F.Implantacao, "Reimplantar", QuemDecide.Voce,
                    new[] { Escolha("motivo", "Motivo (troca de dono, novo CNPJ, recomeço)", d => d.Motivo, MotivosReimplantar) },
                    "Checklist novo; a cobrança continua; sem novo teste.", "Com o checklist fechado, registre a volta.", EfeitoPassagem.Reimplantar),
                new(F.Implantacao, F.Operando, "Voltar a operar (reimplantação)", QuemDecide.Voce, new[] { ChecklistSemTrava },
                    "Volta a operar sem novo teste; a cobrança seguiu durante a reimplantação.", "—", EfeitoPassagem.VoltarAOperar),
            };

            foreach (var de in new[] { F.Conversa, F.Proposta, F.Implantacao, F.Teste })
            {
                list.Add(new(de, F.Perdido, "Perder", QuemDecide.Voce,
                    new[] { Texto("motivo", "Motivo", d => d.Motivo), Escolha("tentarDeNovo", "Tentar de novo (não, 30 ou 90 dias)", d => d.TentarDeNovo, TentarDeNovo) },
                    "Sai do quadro; se a loja já existia, é desativada (dados guardados por 90 dias); o motivo entra nos números.",
                    "Retomar.", EfeitoPassagem.Perder));
            }

            list.Add(new(F.Perdido, F.Conversa, "Retomar", QuemDecide.Voce, Array.Empty<RequisitoDef>(),
                "Volta com o histórico; precisa de próximo passo. A data de tentar de novo é lembrete: não retoma sozinha.", "—", EfeitoPassagem.Retomar));
            list.Add(new(F.Encerrado, F.Conversa, "Quer voltar", QuemDecide.Voce, Array.Empty<RequisitoDef>(),
                "Volta com todo o histórico.", "—", EfeitoPassagem.QuerVoltar));
            return list;
        }

        // ---- requisitos

        private static RequisitoDef Texto(string chave, string descricao, Func<DadosPassagem, string?> valor) =>
            new(chave, descricao, c => string.IsNullOrWhiteSpace(valor(c.Dados)) ? $"Informe: {descricao.ToLowerInvariant()}." : null);

        private static RequisitoDef Escolha(string chave, string descricao, Func<DadosPassagem, string?> valor, string[] opcoes) =>
            new(chave, descricao, c =>
            {
                var v = (valor(c.Dados) ?? string.Empty).Trim().ToLowerInvariant();
                return Array.IndexOf(opcoes, v) >= 0 ? null : $"Escolha {string.Join(", ", opcoes)}.";
            });

        private static readonly RequisitoDef QuemDecideRequisito = new("quemDecide", "Quem decide",
            c => string.IsNullOrWhiteSpace(c.Dados.QuemDecide ?? c.Loja.QuemDecide) ? "Informe quem decide na loja." : null);

        private static readonly RequisitoDef Aceite = new("aceite", "Aceite (verbal, WhatsApp, assinado)",
            c => Array.IndexOf(Aceites, (c.Dados.Aceite ?? string.Empty).Trim().ToLowerInvariant()) >= 0
                ? null : "Registre o aceite: verbal, whatsapp ou assinado.");

        private static readonly RequisitoDef PlanoDoCatalogo = new("plano", "Plano do catálogo",
            c => Array.IndexOf(SituacoesComerciais.Planos, (c.Dados.Plano ?? string.Empty).Trim().ToLowerInvariant()) >= 0
                ? null : "Escolha um plano do catálogo.");

        private static readonly RequisitoDef FormaPagamento = new("formaPagamento", "Forma de pagamento",
            c => Array.IndexOf(FormasPagamento, (c.Dados.FormaPagamento ?? string.Empty).Trim().ToLowerInvariant()) >= 0
                ? null : "Escolha a forma de pagamento: pix, cartao ou boleto.");

        private static readonly RequisitoDef Documento = new("documento", "CNPJ ou CPF",
            c => DocumentoRegras.Valido(c.Dados.Documento ?? c.Loja.Documento) ? null : "Informe um CNPJ ou CPF válido.");

        private const string CaminhoRegistrarEnvio = "Registrar envio";

        // So o registro de envio liga este fato: nao vem do corpo da passagem.
        private static readonly RequisitoDef PropostaEnviada = new("propostaEnviada", "Proposta enviada e registrada",
            c => c.Loja.EnvioRegistrado ? null : "Registre o envio da proposta: a fase muda junto, sem precisar mover o cartão.");

        private static readonly RequisitoDef MotivoDeVolta = new("motivo", "Motivo (esfriou, mudou quem decide)", c =>
        {
            var v = (c.Dados.Motivo ?? string.Empty).Trim().ToLowerInvariant();
            if (v == "renegociar") return "Para renegociar, faça uma revisão da proposta: a fase não muda.";
            return Array.IndexOf(Motivos, v) >= 0 ? null : $"Escolha {string.Join(", ", Motivos)}.";
        });

        private static readonly RequisitoDef ValorCombinado = new("valor", "Mensalidade combinada",
            c => PropostaRegras.ValorDefinido(c.Dados.ValorMensal, c.Dados.ValorZeroConsciente == true)
                ? null : "Informe a mensalidade combinada. R$ 0 só com confirmação consciente.");

        private static readonly RequisitoDef ValorDaProposta = new("valor", "Mensalidade da proposta",
            c => !c.Loja.TemProposta || c.Loja.PropostaComValor ? null : "A proposta enviada não tem mensalidade: revise e registre o envio de novo.");

        private static readonly RequisitoDef PropostaRegistrada = new("proposta", "Proposta enviada",
            c => c.Loja.TemProposta ? null : "Não há proposta enviada para aceitar.");

        private static readonly RequisitoDef LojaCriada = new("loja", "Loja criada",
            c => c.Loja.TemLoja ? null : "A loja ainda não foi criada.");

        private static readonly RequisitoDef ChecklistSemTrava = new("checklist", "Checklist sem pendência que trava",
            c => c.Loja.PendenciasBloqueantes switch
            {
                null => "Não foi possível conferir o checklist da loja.",
                0 => null,
                var n => $"Ainda faltam {n} item(ns) obrigatório(s) no checklist."
            });

        private static readonly RequisitoDef DiasDeProrrogacao = new("dias", "+7 ou +15 dias",
            c => c.Dados.Dias is { } d && Array.IndexOf(DiasProrrogacao, d) >= 0 ? null : "Prorrogue por 7 ou 15 dias.");

        private static readonly RequisitoDef SemCobrancaEmAberto = new("cobranca", "Sem cobrança em aberto",
            c => c.Loja.CobrancaEmAberto ? "Há cobrança em aberto: resolva antes de encerrar." : null);

        private static readonly RequisitoDef LojaAvisada = new("lojaAvisada", "Loja avisada",
            c => c.Dados.LojaAvisada == true ? null : "Confirme que a loja foi avisada.");

        private static readonly RequisitoDef DataDeVolta = new("dataVolta", "Data de volta",
            c => c.Dados.DataVolta is { } d && d > FusoComercial.Data(c.Agora) ? null : "Informe uma data de volta futura.");
    }

    /// <summary>CPF (11 digitos) ou CNPJ (14 digitos) com digitos verificadores.</summary>
    public static class DocumentoRegras
    {
        public static string? Digitos(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return null;
            var d = new string(valor.Where(char.IsDigit).ToArray());
            return d.Length == 0 ? null : d;
        }

        public static bool Valido(string? valor)
        {
            var d = Digitos(valor);
            if (d == null || d.Distinct().Count() == 1) return false;
            return d.Length switch
            {
                11 => Confere(d, 9, new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2 }) && Confere(d, 10, new[] { 11, 10, 9, 8, 7, 6, 5, 4, 3, 2 }),
                14 => Confere(d, 12, new[] { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 }) && Confere(d, 13, new[] { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 }),
                _ => false
            };
        }

        private static bool Confere(string d, int posicao, int[] pesos)
        {
            var soma = 0;
            for (var i = 0; i < pesos.Length; i++) soma += (d[i] - '0') * pesos[i];
            var resto = soma % 11;
            var dv = resto < 2 ? 0 : 11 - resto;
            return d[posicao] - '0' == dv;
        }
    }
}
