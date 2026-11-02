using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Service;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <summary>
    /// Varios numeros de teste da Zippy. Cada um e um canal com <c>Teste</c>, atende uma loja por vez (o destino) e no maximo um e o
    /// principal (<c>Central</c>, o do contrato antigo e do fallback). O destino so muda por ID de loja existente, escolhido pelo operador:
    /// nunca e inferido por slug, nome ou perfil.
    /// </summary>
    public sealed partial class CanalCentralService
    {
        public async Task<IReadOnlyList<CanalTesteView>> ListarTesteAsync(AtendimentoAtor ator)
        {
            ExigirGestao(ator);
            return (await _canais.ListarTesteAsync()).Select(VerTeste).ToList();
        }

        public async Task<CanalTesteView> CriarTesteAsync(AtendimentoAtor ator, CanalTesteRequest request)
        {
            ExigirGestao(ator);
            if (request == null) throw new ArgumentException("Informe os dados do numero de teste.");

            var finalidade = ValidarFinalidade(request.Finalidade) ?? FinalidadeTeste.Geral;
            ExigirDestino(request.EstabelecimentoId);
            await ExigirLojaAsync(request.EstabelecimentoId);

            var erros = AtendimentoRegras.ValidarCanal(request.PhoneNumberId, request.Numero, null);
            var token = CifrarToken(request.Token, erros);
            if (erros.Count > 0) throw new RequestValidationException("Dados invalidos.", erros);

            var phoneNumberId = request.PhoneNumberId!.Trim();
            var numero = AtendimentoRegras.NormalizarNumero(request.Numero)!;

            var porId = await _canais.ObterPorPhoneNumberIdAsync(phoneNumberId);
            var porNumero = await _canais.ObterPorNumeroAsync(numero.TrimStart('+'));
            var dono = porId ?? porNumero;
            if (dono != null)
            {
                var campo = porId != null ? "phoneNumberId" : "numero";
                throw dono.DeTeste
                    ? new CanalConflitoException(campo, "Este numero ja esta cadastrado como numero de teste. Altere o existente.")
                    : new CanalConflitoException(campo, "Este numero ja pertence a uma loja e nao pode ser numero de teste.");
            }

            var semPrincipal = await _canais.ObterCentralAsync() == null;
            var canal = new CanalWhatsapp
            {
                Id = Guid.NewGuid(), IdEstabelecimento = request.EstabelecimentoId, PhoneNumberId = phoneNumberId, NumeroE164 = numero,
                Nome = string.IsNullOrWhiteSpace(request.Nome) ? "Numero de teste" : request.Nome.Trim(), TokenCifrado = token,
                Teste = true, Central = semPrincipal, FinalidadeTeste = finalidade, Origem = OrigemCanal.Central,
                Status = StatusCanal.Configurando, ModoAtendimento = ModoAtendimento.Hibrido
            };

            try
            {
                var servicos = await ServicosAtivosAsync(request.EstabelecimentoId);
                await _canais.CriarAsync(canal, servicos);
                await _catalogo.GarantirModulosAsync(request.EstabelecimentoId, new[] { ModuloWhatsapp });
                await _canais.RegistrarAuditoriaAsync(canal.Id, "teste_criado", null, request.EstabelecimentoId, ator.UsuarioId,
                    JsonSerializer.Serialize(new { numero, finalidade, principal = semPrincipal, tokenNovo = token != null }));
                if (request.Principal == true && !semPrincipal)
                {
                    await _canais.AplicarTesteAsync(new AlteracaoTeste(canal.Id, "teste_principal", ator.UsuarioId, null, null, null, null, null, true, null,
                        JsonSerializer.Serialize(new { principal = true })));
                }
            }
            catch (CanalConflitoException ex)
            {
                _logger.LogWarning("[canal] ev=conflito acao=criar_teste campo={Campo}", ex.Campo);
                throw;
            }

            _logger.LogInformation("[canal] ev=teste_criado canal={Canal} loja={Loja} usuario={Usuario}", canal.Id, request.EstabelecimentoId, ator.UsuarioId);
            return VerTeste(await _canais.ObterAsync(canal.Id) ?? canal);
        }

        public async Task<CanalTesteView> AtualizarTesteAsync(AtendimentoAtor ator, Guid id, CanalTesteAtualizacao request)
        {
            ExigirGestao(ator);
            if (request == null) throw new ArgumentException("Informe o que alterar.");

            var canal = await ObterTesteOuFalharAsync(id);

            var finalidade = ValidarFinalidade(request.Finalidade);
            if (request.Status != null && request.Status is not (StatusCanal.Ativo or StatusCanal.Inativo))
            {
                throw new ArgumentException("Status invalido. Use ativo ou inativo.");
            }

            if (request.EstabelecimentoId == Guid.Empty) throw new ArgumentException("Informe um estabelecimento valido.");

            var erros = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var token = CifrarToken(request.Token, erros);
            var moveu = request.EstabelecimentoId.HasValue && request.EstabelecimentoId.Value != canal.IdEstabelecimento;
            if (moveu) await ExigirLojaAsync(request.EstabelecimentoId!.Value);

            if (finalidade == FinalidadeTeste.Gestao && !request.EstabelecimentoId.HasValue)
            {
                Erro(erros, "estabelecimentoId", "Para usar o numero na Gestao, escolha a loja de Gestao pelo ID: ela nunca e inferida.");
            }

            // Ativar exige a Meta ter confirmado este destino e estas credenciais; mover ou trocar o token zera a verificacao.
            if (request.Status == StatusCanal.Ativo && (moveu || token != null || canal.VerificadoEm == null))
            {
                Erro(erros, "status", "Verifique o numero na Meta antes de ativar.");
            }

            if (canal.Central && (request.Principal == false || request.Status == StatusCanal.Inativo))
            {
                Erro(erros, "principal", "Este e o numero principal: escolha outro como principal antes de tirar este de uso.");
            }

            if (request.Principal == true && (request.Status == StatusCanal.Inativo || canal.Status == StatusCanal.Inativo && request.Status != StatusCanal.Ativo))
            {
                Erro(erros, "principal", "Um numero inativo nao pode ser o principal.");
            }

            if (erros.Count > 0) throw new RequestValidationException("Dados invalidos.", erros);

            var servicos = moveu ? await ServicosAtivosAsync(request.EstabelecimentoId!.Value) : null;
            if (moveu) await _catalogo.GarantirModulosAsync(request.EstabelecimentoId!.Value, new[] { ModuloWhatsapp });

            var nome = string.IsNullOrWhiteSpace(request.Nome) ? null : request.Nome.Trim();
            var acao = moveu ? "teste_destino_alterado" : "teste_atualizado";
            var detalhe = JsonSerializer.Serialize(new
            {
                nome, status = request.Status, finalidade, principal = request.Principal, tokenNovo = token != null, destinoAnterior = canal.IdEstabelecimento
            });

            try
            {
                await _canais.AplicarTesteAsync(new AlteracaoTeste(
                    id, acao, ator.UsuarioId, nome, request.Status, moveu ? request.EstabelecimentoId : null, servicos, finalidade,
                    request.Principal == true ? true : (bool?)null, token, detalhe));
            }
            catch (CanalConflitoException ex)
            {
                _logger.LogWarning("[canal] ev=conflito acao=atualizar_teste campo={Campo}", ex.Campo);
                throw;
            }

            _logger.LogInformation("[canal] ev={Acao} canal={Canal} de_loja={Origem} para_loja={Destino} usuario={Usuario}",
                acao, id, canal.IdEstabelecimento, request.EstabelecimentoId ?? canal.IdEstabelecimento, ator.UsuarioId);
            return VerTeste(await _canais.ObterAsync(id) ?? canal);
        }

        public async Task<CanalTesteVerificacao> VerificarTesteAsync(AtendimentoAtor ator, Guid id)
        {
            ExigirGestao(ator);
            var canal = await ObterTesteOuFalharAsync(id);
            if (_verificador == null) throw new InvalidOperationException("Verificador da Meta nao configurado.");

            var resultado = await _verificador.VerificarAsync(canal);
            var atual = await _canais.ObterAsync(id) ?? canal;
            await _canais.RegistrarAuditoriaAsync(id, "teste_verificado", atual.IdEstabelecimento, null, ator.UsuarioId,
                JsonSerializer.Serialize(new { ok = resultado.Ok, codigo = resultado.Codigo }));
            return new CanalTesteVerificacao(
                resultado.Ok, resultado.Mensagem, resultado.NumeroNaMeta, resultado.NomeVerificado, resultado.Codigo, resultado.Campo,
                atual.TemToken, WebhookEstado.NaoAplicavel, VerTeste(atual));
        }

        private async Task<CanalWhatsapp> ObterTesteOuFalharAsync(Guid id)
        {
            var canal = await _canais.ObterAsync(id);
            if (canal == null || !canal.DeTeste) throw new KeyNotFoundException("Numero de teste nao encontrado.");
            return canal;
        }

        private async Task ExigirLojaAsync(Guid estabelecimentoId)
        {
            if (!await _catalogo.LojaExisteAsync(estabelecimentoId)) throw new KeyNotFoundException("Estabelecimento de destino nao encontrado.");
        }

        private static void ExigirDestino(Guid estabelecimentoId)
        {
            if (estabelecimentoId == Guid.Empty) throw new ArgumentException("Informe o estabelecimento que recebe as mensagens do numero.");
        }

        private static string? ValidarFinalidade(string? finalidade)
        {
            if (finalidade == null) return null;
            if (!FinalidadeTeste.Valida(finalidade)) throw new ArgumentException("Finalidade invalida. Use geral ou gestao.");
            return finalidade;
        }

        private async Task<List<string>> ServicosAtivosAsync(Guid estabelecimentoId) =>
            (await _catalogo.ListarServicosDaLojaAsync(estabelecimentoId)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();

        private static void Erro(Dictionary<string, List<string>> erros, string campo, string mensagem)
        {
            if (!erros.TryGetValue(campo, out var lista)) erros[campo] = lista = new List<string>();
            lista.Add(mensagem);
        }

        private static CanalTesteView VerTeste(CanalWhatsapp c) => new(
            c.Id, c.IdEstabelecimento, c.PhoneNumberId, c.NumeroE164, c.Nome, c.Status, c.Central, c.FinalidadeTeste, c.TemToken,
            c.VerificadoEm, c.UltimoRecebimentoEm, c.UltimoEnvioOkEm, c.UltimoErro, c.UltimoErroEm, c.Servicos, WebhookEstado.NaoAplicavel);
    }
}
