using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Atendimento;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento.Motor
{
    /// <summary>Uma mensagem do cliente ja gravada na conversa e pronta para o atendimento automatico.</summary>
    public sealed record MensagemRecebida(
        Guid ConversaId, CanalWhatsapp Canal, string Telefone, string Texto, string? Interpretado, bool PrimeiraMensagem, DateTime QuandoUtc);

    public interface IExecutorDeAtendimento
    {
        Task ExecutarAsync(MensagemRecebida mensagem);

        /// <summary>Roda o motor como se o cliente tivesse escrito, sem enviar nada nem gravar: serve ao "testar atendimento" do dono.</summary>
        Task<ResultadoMotor> SimularAsync(Guid loja, string modo, IReadOnlyList<string> servicos, string texto, EstadoFluxo? estado, bool primeiraMensagem);
    }

    /// <summary>
    /// Junta o motor (decisao pura) ao mundo real: carrega loja, horario e estado, manda decidir, envia as respostas,
    /// chama a equipe quando for o caso e guarda o novo estado. Cada atendimento vira UMA linha de log com a regra que
    /// decidiu e as acoes tomadas: e por ela que se entende, no Render, por que o bot respondeu o que respondeu.
    /// </summary>
    public sealed class ExecutorDeAtendimento : IExecutorDeAtendimento
    {
        private readonly AtendimentoMotor _motor;
        private readonly IFluxoEstadoRepository _estados;
        private readonly IEnviadorDeRespostas _enviador;
        private readonly IEstabelecimentoRepository _lojas;
        private readonly IAtendimentoRepository _config;
        private readonly IConversationRepository _conversas;
        private readonly IChatRealtimePublisher _tempoReal;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ExecutorDeAtendimento> _logger;

        public ExecutorDeAtendimento(
            AtendimentoMotor motor, IFluxoEstadoRepository estados, IEnviadorDeRespostas enviador, IEstabelecimentoRepository lojas,
            IAtendimentoRepository config, IConversationRepository conversas, IChatRealtimePublisher tempoReal,
            IConfiguration configuration, ILogger<ExecutorDeAtendimento> logger)
        {
            _motor = motor;
            _estados = estados;
            _enviador = enviador;
            _lojas = lojas;
            _config = config;
            _conversas = conversas;
            _tempoReal = tempoReal;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task ExecutarAsync(MensagemRecebida mensagem)
        {
            var relogio = Stopwatch.StartNew();
            var canal = mensagem.Canal;
            var loja = canal.IdEstabelecimento;

            var estado = await _estados.ObterAsync(mensagem.ConversaId);
            var (entrada, aberto) = await MontarEntradaAsync(
                mensagem.ConversaId, loja, canal.Id, canal.ModoAtendimento, canal.Servicos, mensagem.Texto, mensagem.Interpretado,
                mensagem.PrimeiraMensagem, mensagem.QuandoUtc);

            var decisao = _motor.Decidir(entrada, estado, DateTime.UtcNow);

            var enviadas = 0;
            foreach (var acao in decisao.Acoes)
            {
                if (acao is AcaoChamarAtendente chamar)
                {
                    await ChamarAtendenteAsync(mensagem, chamar);
                }
                else if (await _enviador.EnviarAsync(mensagem.ConversaId, canal, mensagem.Telefone, acao))
                {
                    enviadas++;
                }
            }

            await _estados.SalvarAsync(mensagem.ConversaId, decisao.Estado, decisao.Fluxo);

            _logger.LogInformation(
                "[atend] ev=decisao conversa={Conversa} canal={Canal} loja={Loja} modo={Modo} servicos={Servicos} primeira={Primeira} aberto={Aberto} " +
                "fluxo={Fluxo} passo={Passo} regra={Regra} acoes={Acoes} enviadas={Enviadas} ms={Ms}",
                mensagem.ConversaId, canal.Id, loja, canal.ModoAtendimento, string.Join(",", canal.Servicos), mensagem.PrimeiraMensagem, aberto,
                decisao.Fluxo, decisao.Passo, decisao.Regra, string.Join(",", decisao.Acoes.Select(NomeDaAcao)), enviadas, relogio.ElapsedMilliseconds);
        }

        public async Task<ResultadoMotor> SimularAsync(
            Guid loja, string modo, IReadOnlyList<string> servicos, string texto, EstadoFluxo? estado, bool primeiraMensagem)
        {
            var (entrada, _) = await MontarEntradaAsync(Guid.Empty, loja, Guid.Empty, modo, servicos, texto, texto, primeiraMensagem, DateTime.UtcNow);
            var decisao = _motor.Decidir(entrada, estado, DateTime.UtcNow);
            _logger.LogInformation("[atend] ev=simulacao loja={Loja} modo={Modo} fluxo={Fluxo} passo={Passo} regra={Regra}", loja, modo, decisao.Fluxo, decisao.Passo, decisao.Regra);
            return decisao;
        }

        private async Task<(EntradaMotor Entrada, bool Aberto)> MontarEntradaAsync(
            Guid conversa, Guid loja, Guid canal, string modo, IReadOnlyList<string> servicos, string texto, string? interpretado,
            bool primeira, DateTime quandoUtc)
        {
            var nome = await _lojas.ObterNomeFantasiaAsync(loja) ?? "nossa loja";
            var config = await SafeConfigAsync(loja);
            var aceita = await _lojas.ObterAceitaPedidosAsync(loja) ?? true;
            var situacao = PedidosAbertosRules.Avaliar(aceita, config?.HorarioAtendimento, AtendimentoConfigRules.ParaHorarioLocal(quandoUtc));
            var aberto = situacao.Aberto;
            var m = config?.Mensagens;
            string? Txt(string chave) => m != null && m.TryGetValue(chave, out var v) ? v : null;
            var textos = new TextosMotor(
                Txt(MensagensDoAtendimento.Menu), Txt(MensagensDoAtendimento.Cardapio), Txt(MensagensDoAtendimento.Atendente),
                Txt(MensagensDoAtendimento.Agendamento), Txt(MensagensDoAtendimento.SemServico),
                Txt(MensagensDoAtendimento.CardapioFechado));

            var entrada = new EntradaMotor(
                conversa, loja, canal, nome, modo, servicos, texto, interpretado, primeira,
                ForaDoHorario: !aberto, config?.MensagemForaHorario, config?.SaudacaoHumano, MontarUrlDoCardapio(loja), textos, situacao);
            return (entrada, aberto);
        }

        // ---- passar para a equipe ------------------------------------------------------------------------------------

        private async Task ChamarAtendenteAsync(MensagemRecebida mensagem, AcaoChamarAtendente chamar)
        {
            var loja = mensagem.Canal.IdEstabelecimento;
            try
            {
                var atualizado = await _conversas.AtualizarStatusAtendimentoAsync(mensagem.ConversaId, "aguardando_interno", null, null, loja);
                await _conversas.RegistrarEventoAsync(mensagem.ConversaId, new ConversationEventDto
                {
                    Id = Guid.NewGuid(),
                    Type = "status_changed",
                    At = DateTime.UtcNow,
                    FromStatus = "com_bot",
                    ToStatus = "aguardando_interno",
                    Reason = chamar.Motivo,
                    Source = "bot",
                    ActorName = "Bot"
                }, loja);
                await _tempoReal.ConversaAtualizadaAsync(mensagem.ConversaId, loja, "chamar_atendente");

                _logger.LogInformation(
                    "[atend] ev=atendente_chamado conversa={Conversa} loja={Loja} motivo=\"{Motivo}\" atualizado={Atualizado}",
                    mensagem.ConversaId, loja, chamar.Motivo, atualizado);
            }
            catch (Exception ex)
            {
                // A resposta ao cliente ja saiu; se a fila da equipe falhar a conversa continua visivel no chat.
                _logger.LogError(ex, "[atend] ev=erro etapa=chamar_atendente conversa={Conversa} loja={Loja}", mensagem.ConversaId, loja);
            }
        }

        // ---- dados de apoio ------------------------------------------------------------------------------------------------

        private async Task<AtendimentoConfigDto?> SafeConfigAsync(Guid loja)
        {
            try
            {
                return await _config.GetConfigAsync(loja);
            }
            catch (Exception ex)
            {
                // Sem a configuracao o atendimento usa os textos padrao e considera a loja aberta: melhor que ficar mudo.
                _logger.LogWarning(ex, "[atend] ev=sem_config loja={Loja}", loja);
                return null;
            }
        }

        private string? MontarUrlDoCardapio(Guid loja)
        {
            var baseUrl = _configuration["Atendimento:CardapioBaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.StartsWith("__", StringComparison.Ordinal))
            {
                _logger.LogWarning("[atend] ev=sem_url_do_cardapio dica=\"configure Atendimento__CardapioBaseUrl (endereco publico do site, ex.: https://app.zippygo.com.br)\"");
                return null;
            }

            return $"{baseUrl.TrimEnd('/')}/cardapio/{loja}";
        }

        private static string NomeDaAcao(AcaoMotor acao) => acao switch
        {
            AcaoResponder => "texto",
            AcaoBotoes => "botoes",
            AcaoChamarAtendente => "chamar_atendente",
            _ => acao.GetType().Name
        };
    }
}
