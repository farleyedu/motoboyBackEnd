using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Acompanhamento.Fases;

namespace APIBack.Acompanhamento.Saude
{
    /// <summary>Chaves das regras de saude gravadas em acompanhamento_saude_regra (limites auditaveis).</summary>
    public static class ChavesSaude
    {
        public const string QuedaPercentual = "queda_percentual";
        public const string QuedaBaseSemanalMinima = "queda_base_semanal_minima";
        public const string DiasSemPedido = "dias_sem_pedido";
        public const string VencimentoDias = "vencimento_dias";
        public const string RespostaPendenteDias = "resposta_pendente_dias";
        public const string DiasSemPasso = "dias_sem_passo";
        public const string ImplantacaoTravadaDias = "implantacao_travada_dias";
        public const string PropostaVenceEmDias = "proposta_vence_em_dias";
        public const string TesteUsoMinimoPedidos = "teste_uso_minimo_pedidos";
        public const string TesteUsoAposDias = "teste_uso_apos_dias";
        public const string TesteAtencaoAteDias = "teste_atencao_ate_dias";
        public const string TesteAjudaAteDias = "teste_ajuda_ate_dias";
        public const string EscalonarSemPagamentoDias = "escalonar_sem_pagamento_dias";
    }

    public sealed record RegraSaudeDef(string Chave, string Descricao, string Unidade, int Minimo, int Maximo, bool Anulavel, int? Padrao);

    /// <summary>
    /// Limites do calculo de saude. Sao a unica fonte: o servidor aplica no Quadro e na fila Hoje. O padrao e o que a regra do
    /// documento diz; <see cref="EscalonarSemPagamentoDias"/> nasce desligado (nulo) ate o proprietario definir N.
    /// </summary>
    public sealed record LimitesSaude
    {
        public int Versao { get; init; }
        public int QuedaPercentual { get; init; } = 40;
        /// <summary>Media semanal minima da propria loja para a queda valer (abaixo disso, 1 pedido a menos vira "queda").</summary>
        public int QuedaBaseSemanalMinima { get; init; } = 10;
        public int DiasSemPedido { get; init; } = 7;
        public int VencimentoDias { get; init; } = 10;
        public int RespostaPendenteDias { get; init; } = 1;
        public int DiasSemPasso { get; init; } = 5;
        public int ImplantacaoTravadaDias { get; init; } = 14;
        public int PropostaVenceEmDias { get; init; } = 3;
        public int TesteUsoMinimoPedidos { get; init; } = 10;
        public int TesteUsoAposDias { get; init; } = 7;
        /// <summary>Teste sem pagamento: faltando ate N dias (inclusive) entra em Atencao (padrao 4 e 3).</summary>
        public int TesteAtencaoAteDias { get; init; } = 4;
        /// <summary>Teste sem pagamento: faltando ate N dias (inclusive) entra em Precisa de ajuda (padrao 2, 1 e 0).</summary>
        public int TesteAjudaAteDias { get; init; } = 2;
        /// <summary>Dias de operacao sem pagamento apos o teste para escalar ao gestor. Nulo = desligado.</summary>
        public int? EscalonarSemPagamentoDias { get; init; }

        public static readonly IReadOnlyList<RegraSaudeDef> Definicoes = new[]
        {
            new RegraSaudeDef(ChavesSaude.QuedaPercentual, "Queda de pedidos da propria loja (ultimos 7 dias contra a media das 3 semanas antes) que vira Atencao", "%", 1, 100, false, 40),
            new RegraSaudeDef(ChavesSaude.QuedaBaseSemanalMinima, "Media semanal minima da loja para a queda valer", "pedidos/semana", 0, 10000, false, 10),
            new RegraSaudeDef(ChavesSaude.DiasSemPedido, "Dias seguidos sem pedido que viram Precisa de ajuda", "dias", 1, 90, false, 7),
            new RegraSaudeDef(ChavesSaude.VencimentoDias, "Cobranca que vence em ate N dias vira Atencao", "dias", 1, 60, false, 10),
            new RegraSaudeDef(ChavesSaude.RespostaPendenteDias, "Chamado aguardando resposta da Zippy ha N dias vira Atencao", "dias", 1, 30, false, 1),
            new RegraSaudeDef(ChavesSaude.DiasSemPasso, "Dias sem nenhum passo da equipe numa loja que espera um passo", "dias", 1, 90, false, 5),
            new RegraSaudeDef(ChavesSaude.ImplantacaoTravadaDias, "Dias em implantacao para considerar travada", "dias", 1, 180, false, 14),
            new RegraSaudeDef(ChavesSaude.PropostaVenceEmDias, "Proposta que vence em ate N dias vira Atencao (vencida: Precisa de ajuda)", "dias", 0, 60, false, 3),
            new RegraSaudeDef(ChavesSaude.TesteUsoMinimoPedidos, "Pedidos esperados no teste depois de TesteUsoAposDias", "pedidos", 0, 100000, false, 10),
            new RegraSaudeDef(ChavesSaude.TesteUsoAposDias, "Dias de teste antes de cobrar uso minimo", "dias", 0, 60, false, 7),
            new RegraSaudeDef(ChavesSaude.TesteAtencaoAteDias, "Teste sem pagamento: faltando ate N dias entra em Atencao", "dias", 1, 14, false, 4),
            new RegraSaudeDef(ChavesSaude.TesteAjudaAteDias, "Teste sem pagamento: faltando ate N dias entra em Precisa de ajuda", "dias", 0, 13, false, 2),
            new RegraSaudeDef(ChavesSaude.EscalonarSemPagamentoDias, "Dias operando sem pagamento depois do teste para escalar ao gestor (vazio = desligado)", "dias", 1, 365, true, null),
        };

        public IReadOnlyDictionary<string, int?> ComoDicionario() => new Dictionary<string, int?>
        {
            [ChavesSaude.QuedaPercentual] = QuedaPercentual,
            [ChavesSaude.QuedaBaseSemanalMinima] = QuedaBaseSemanalMinima,
            [ChavesSaude.DiasSemPedido] = DiasSemPedido,
            [ChavesSaude.VencimentoDias] = VencimentoDias,
            [ChavesSaude.RespostaPendenteDias] = RespostaPendenteDias,
            [ChavesSaude.DiasSemPasso] = DiasSemPasso,
            [ChavesSaude.ImplantacaoTravadaDias] = ImplantacaoTravadaDias,
            [ChavesSaude.PropostaVenceEmDias] = PropostaVenceEmDias,
            [ChavesSaude.TesteUsoMinimoPedidos] = TesteUsoMinimoPedidos,
            [ChavesSaude.TesteUsoAposDias] = TesteUsoAposDias,
            [ChavesSaude.TesteAtencaoAteDias] = TesteAtencaoAteDias,
            [ChavesSaude.TesteAjudaAteDias] = TesteAjudaAteDias,
            [ChavesSaude.EscalonarSemPagamentoDias] = EscalonarSemPagamentoDias,
        };

        /// <summary>Aplica valores (chave -> valor; nulo so vale para chave anulavel) sobre estes limites. Devolve os erros de validacao.</summary>
        public (LimitesSaude Limites, IReadOnlyList<string> Erros) Aplicar(IReadOnlyDictionary<string, int?> valores)
        {
            var erros = new List<string>();
            var atual = ComoDicionario().ToDictionary(p => p.Key, p => p.Value);
            foreach (var (chave, valor) in valores)
            {
                var def = Definicoes.FirstOrDefault(d => d.Chave == chave);
                if (def == null) { erros.Add($"Regra desconhecida: {chave}."); continue; }
                if (valor == null)
                {
                    if (!def.Anulavel) erros.Add($"{chave} nao pode ficar vazia.");
                    else atual[chave] = null;
                    continue;
                }
                if (valor < def.Minimo || valor > def.Maximo) { erros.Add($"{chave} deve estar entre {def.Minimo} e {def.Maximo}."); continue; }
                atual[chave] = valor;
            }
            if (atual[ChavesSaude.TesteAjudaAteDias] >= atual[ChavesSaude.TesteAtencaoAteDias])
                erros.Add($"{ChavesSaude.TesteAjudaAteDias} deve ser menor que {ChavesSaude.TesteAtencaoAteDias}.");
            return (new LimitesSaude
            {
                Versao = Versao,
                QuedaPercentual = atual[ChavesSaude.QuedaPercentual]!.Value,
                QuedaBaseSemanalMinima = atual[ChavesSaude.QuedaBaseSemanalMinima]!.Value,
                DiasSemPedido = atual[ChavesSaude.DiasSemPedido]!.Value,
                VencimentoDias = atual[ChavesSaude.VencimentoDias]!.Value,
                RespostaPendenteDias = atual[ChavesSaude.RespostaPendenteDias]!.Value,
                DiasSemPasso = atual[ChavesSaude.DiasSemPasso]!.Value,
                ImplantacaoTravadaDias = atual[ChavesSaude.ImplantacaoTravadaDias]!.Value,
                PropostaVenceEmDias = atual[ChavesSaude.PropostaVenceEmDias]!.Value,
                TesteUsoMinimoPedidos = atual[ChavesSaude.TesteUsoMinimoPedidos]!.Value,
                TesteUsoAposDias = atual[ChavesSaude.TesteUsoAposDias]!.Value,
                TesteAtencaoAteDias = atual[ChavesSaude.TesteAtencaoAteDias]!.Value,
                TesteAjudaAteDias = atual[ChavesSaude.TesteAjudaAteDias]!.Value,
                EscalonarSemPagamentoDias = atual[ChavesSaude.EscalonarSemPagamentoDias],
            }, erros);
        }
    }

    /// <summary>Tudo o que o calculo precisa de uma loja (ou oportunidade). Campo nulo = o servidor nao tem o dado, e isso aparece nos motivos.</summary>
    public sealed record SaudeEntrada
    {
        public Guid? AcompanhamentoId { get; init; }
        public Guid? LojaId { get; init; }
        public Guid? OportunidadeId { get; init; }
        public string Nome { get; init; } = string.Empty;
        /// <summary>Fase gravada. Nulo = a loja ainda nao tem acompanhamento (vale a fase que a situacao impoe).</summary>
        public string? Fase { get; init; }
        public DateTimeOffset? FaseDesde { get; init; }
        public string? Situacao { get; init; }
        public DateTimeOffset? TesteTerminaEm { get; init; }
        public DateTimeOffset? CriadaEm { get; init; }
        public DateTimeOffset? OperacaoIniciadaEm { get; init; }
        public DateOnly? PausaAte { get; init; }
        public int Pedidos7d { get; init; }
        public int Pedidos28d { get; init; }
        public int DiasComPedido28d { get; init; }
        public DateTimeOffset? UltimoPedidoEm { get; init; }
        /// <summary>Plano da assinatura pendente ou ativa. Nulo = nenhum plano escolhido.</summary>
        public string? PlanoContratado { get; init; }
        /// <summary>Algum ciclo da assinatura foi pago (fonte: assinatura_ciclo, nunca lancamento manual do CRM).</summary>
        public bool PagamentoConfirmado { get; init; }
        public decimal ValorAtrasado { get; init; }
        public DateOnly? AtrasadoDesde { get; init; }
        public DateOnly? ProximoVencimento { get; init; }
        public DateTimeOffset? ChamadoPendenteDesde { get; init; }
        public bool ChamadoPrazoVencido { get; init; }
        public DateTimeOffset? UltimoPassoEm { get; init; }
        public DateOnly? PropostaValidadeAte { get; init; }
        public IReadOnlyList<string> Modulos { get; init; } = Array.Empty<string>();
        public string? FaixaGravada { get; init; }

        public string FaseEfetiva => Fase ?? FasesAcompanhamento.PadraoDaSituacao(Situacao ?? Service.SituacoesComerciais.Ativo);
    }

    /// <summary>Um motivo da faixa: o que foi visto, de onde veio e o que faz sair dela.</summary>
    public sealed record MotivoSaude(string Codigo, string Faixa, string Sinal, string Texto, string Origem, string Saida, int? Contador = null);

    /// <summary>A decisao de saude de uma loja. O pior sinal decide a faixa; os motivos vem em ordem de importancia.</summary>
    public sealed record SaudeAvaliacao(
        int Calculo, int Limites, string Faixa, bool Neutra, string Resumo, MotivoSaude? Principal,
        IReadOnlyList<MotivoSaude> Motivos, bool EscalarAoGestor, IReadOnlyList<string> Indisponiveis);
}
