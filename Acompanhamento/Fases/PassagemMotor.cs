using System;
using System.Collections.Generic;
using System.Linq;
using F = APIBack.Acompanhamento.Fases.FasesAcompanhamento;

namespace APIBack.Acompanhamento.Fases
{
    /// <summary>Regras puras das passagens: avalia os requisitos para uma loja e monta o que sera gravado. Sem banco, para testar.</summary>
    public static class PassagemMotor
    {
        public const int DiasValidadeProposta = 7;

        public static PassagensCatalogoDto Catalogo()
        {
            var recusas = new List<RecusaDto>();
            var semTeste = new LojaFatos();
            foreach (var de in F.Todas)
            foreach (var para in F.Todas)
            {
                var recusa = PassagensCatalogo.Recusa(de, para, semTeste with { Fase = de });
                if (recusa != null && recusa.Motivo != "Essa passagem não existe." && !recusa.Motivo.StartsWith("A loja já está em", StringComparison.Ordinal))
                    recusas.Add(new RecusaDto(de, para, recusa.Motivo, recusa.Caminho, recusa.Sequencia));
            }

            var reimplantada = PassagensCatalogo.Recusa(F.Implantacao, F.Teste, semTeste with { Fase = F.Implantacao, TesteConcluido = true })!;
            recusas.Add(new RecusaDto(F.Implantacao, F.Teste, reimplantada.Motivo, reimplantada.Caminho, reimplantada.Sequencia));
            return new PassagensCatalogoDto(F.Todas, F.NoQuadro, PassagensCatalogo.Todas.Select(Definicao).ToList(), recusas);
        }

        public static PassagemDefinicaoDto Definicao(PassagemDef p) =>
            new(p.De, p.Para, p.Nome, p.Quem, p.Requisitos.Select(r => new RequisitoDto(r.Chave, r.Descricao)).ToList(), p.OQueAcontece, p.Volta);

        public static AvaliacaoPassagemDto Avaliar(Guid acompanhamentoId, long versao, LojaFatos loja, string para, DadosPassagem dados, DateTimeOffset agora)
        {
            var de = loja.Fase;
            var recusa = PassagensCatalogo.Recusa(de, para, loja);
            if (recusa != null)
                return new AvaliacaoPassagemDto(acompanhamentoId, de, para, false, false, null, Array.Empty<RequisitoEstadoDto>(),
                    new RecusaDto(de, para, recusa.Motivo, recusa.Caminho, recusa.Sequencia), versao);

            var def = PassagensCatalogo.Obter(de, para)!;
            var contexto = new ContextoPassagem(loja, dados, agora);
            var requisitos = def.Requisitos.Select(r =>
            {
                var pendencia = r.Pendencia(contexto);
                return new RequisitoEstadoDto(r.Chave, r.Descricao, pendencia == null, pendencia);
            }).ToList();
            return new AvaliacaoPassagemDto(acompanhamentoId, de, para, true, requisitos.All(r => r.Pronto), Definicao(def), requisitos, null, versao);
        }

        /// <summary>Recusa (passagem inexistente ou requisito faltando) como 422, com motivo, caminho e pendencias.</summary>
        public static AcompanhamentoException? Recusar(AvaliacaoPassagemDto avaliacao)
        {
            if (avaliacao.Recusa is { } r)
                return new AcompanhamentoException(422, CodigosAcompanhamento.PassagemInexistente, r.Motivo,
                    new RecusaDetalhesDto(r.De, r.Para, r.Motivo, r.Caminho, r.Sequencia, Array.Empty<RequisitoEstadoDto>()));
            if (avaliacao.PodeExecutar) return null;

            var pendentes = avaliacao.Requisitos.Where(x => !x.Pronto).ToList();
            var motivo = "Falta: " + string.Join("; ", pendentes.Select(p => p.Descricao)) + ".";
            return new AcompanhamentoException(422, CodigosAcompanhamento.RequisitosPendentes, motivo,
                new RecusaDetalhesDto(avaliacao.FaseAtual, avaliacao.Para, motivo, avaliacao.Passagem!.Nome,
                    new[] { avaliacao.FaseAtual, avaliacao.Para }, pendentes));
        }

        /// <summary>Monta o que a passagem aprovada grava. So chamar com a avaliacao sem recusa.</summary>
        public static PassagemAplicacao Planejar(LojaFatos loja, string para, DadosPassagem dados, string chave, int? usuarioId, string autorTipo, DateTimeOffset agora)
        {
            var def = PassagensCatalogo.Obter(loja.Fase, para)
                ?? throw new InvalidOperationException($"Passagem {loja.Fase} -> {para} nao existe.");
            var hoje = DateOnly.FromDateTime(agora.UtcDateTime);
            var efeito = def.Efeito;

            // Teste sem compromisso: a loja nasce em implantacao e o teste comeca sozinho quando ela ficar pronta.
            var resultante = efeito == EfeitoPassagem.TesteSemCompromisso ? F.Implantacao : def.Para;
            // A segunda prorrogacao fica pendente ate o gestor aprovar: a data nao muda agora.
            var status = efeito == EfeitoPassagem.ProrrogarTeste && loja.ProrrogacoesNoTeste >= 1
                ? StatusPassagem.PendenteAprovacao
                : StatusPassagem.Feita;

            return new PassagemAplicacao
            {
                Definicao = def,
                De = loja.Fase,
                FaseResultante = resultante,
                Status = status,
                Dados = dados,
                Chave = chave,
                UsuarioId = usuarioId,
                AutorTipo = autorTipo,
                Motivo = Limpo(dados.Motivo) ?? Limpo(dados.OQueFez) ?? (efeito is EfeitoPassagem.FecharNaConversa or EfeitoPassagem.PropostaAceita
                    ? "aceite " + Limpo(dados.Aceite)?.ToLowerInvariant()
                    : null),
                Agora = agora,
                TesteQuandoPronta = efeito == EfeitoPassagem.TesteSemCompromisso,
                DiasProrrogacao = efeito == EfeitoPassagem.ProrrogarTeste ? dados.Dias : null,
                PausaAte = efeito == EfeitoPassagem.DeclararPausa ? dados.DataVolta : null,
                RetomarEm = efeito == EfeitoPassagem.Perder && int.TryParse(dados.TentarDeNovo, out var dias) ? hoje.AddDays(dias) : null,
                SaidaEm = efeito == EfeitoPassagem.PediuParaCancelar ? SaidaEm(dados.Quando, hoje) : null,
                CobrancaIniciaEm = efeito == EfeitoPassagem.AtivarPlano ? hoje.AddDays(1) : null
            };
        }

        /// <summary>Data de saida: 30 dias de aviso, agora, ou fim do ciclo (nulo: o ciclo vem da assinatura).</summary>
        public static DateOnly? SaidaEm(string? quando, DateOnly hoje) => (quando ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "aviso_30_dias" => hoje.AddDays(30),
            "agora" => hoje,
            _ => null
        };

        /// <summary>Fatos da loja a partir do estado gravado e do que so o banco sabe.</summary>
        public static LojaFatos Fatos(AcompanhamentoEstado estado, int? pendenciasBloqueantes, bool cobrancaEmAberto, bool temProposta) => new()
        {
            Fase = estado.Fase,
            TemOportunidade = estado.OportunidadeId.HasValue,
            TemLoja = estado.EstabelecimentoId.HasValue,
            Situacao = estado.SituacaoLoja,
            Documento = estado.Documento,
            QuemDecide = estado.QuemDecide,
            PedidosPorDia = estado.PedidosPorDia,
            DorPrincipal = estado.DorPrincipal,
            PendenciasBloqueantes = pendenciasBloqueantes,
            TesteConcluido = estado.TesteConcluidoEm.HasValue,
            ProrrogacoesNoTeste = estado.ProrrogacoesNoTeste,
            CobrancaEmAberto = cobrancaEmAberto,
            TemProposta = temProposta
        };

        private static string? Limpo(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
    }
}
