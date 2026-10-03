using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Model.Gestao;
using APIBack.Service;

namespace APIBack.Atendimento
{
    /// <summary>Regras puras do catalogo de servicos e dos canais: sem banco, sem rede.</summary>
    public static class AtendimentoRegras
    {
        /// <summary>O ID da Meta tem uns 15 digitos; um telefone tem no maximo 13 (55 + DDD + 9 digitos).</summary>
        private const int MaxDigitosDeTelefone = 13;

        public static bool PhoneNumberIdValido(string? phoneNumberId) =>
            !string.IsNullOrWhiteSpace(phoneNumberId)
            && phoneNumberId.Trim().All(char.IsDigit)
            && phoneNumberId.Trim().Length > MaxDigitosDeTelefone;

        /// <summary>Telefone no formato +DDI DDD numero, ou null quando nao da para entender o numero.</summary>
        public static string? NormalizarNumero(string? numero) => PhoneKey.ToE164(numero);

        /// <summary>Erros de campo para o cadastro de um numero (vazio = valido). Chaves no formato da API.</summary>
        public static Dictionary<string, List<string>> ValidarCanal(string? phoneNumberId, string? numero, string? modo)
        {
            var erros = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                Adicionar(erros, "phoneNumberId", "Informe o Phone Number ID da Meta.");
            }
            else if (!PhoneNumberIdValido(phoneNumberId))
            {
                Adicionar(erros, "phoneNumberId",
                    "O ID da Meta tem so digitos e uns 15 numeros. Isso parece um telefone: o telefone vai no outro campo.");
            }

            if (string.IsNullOrWhiteSpace(numero))
            {
                Adicionar(erros, "numero", "Informe o numero do WhatsApp.");
            }
            else if (NormalizarNumero(numero) == null)
            {
                Adicionar(erros, "numero", "Numero invalido. Use DDI + DDD + numero, por exemplo 5534991480112.");
            }

            if (modo != null && !ModoAtendimento.Valido(modo))
            {
                Adicionar(erros, "modoAtendimento", "Modo invalido. Use bot, humano ou hibrido.");
            }

            return erros;
        }

        /// <summary>
        /// Modulos que precisam estar ligados para os servicos escolhidos funcionarem: os que cada servico exige mais o
        /// que esses modulos exigem (DELIVERY -> PEDIDOS). Nunca remove modulo; so acrescenta.
        /// </summary>
        public static IReadOnlyCollection<string> ModulosParaServicos(
            IEnumerable<ServicoCatalogoItem> catalogo, IEnumerable<string> servicosAtivos)
        {
            var escolhidos = new HashSet<string>(servicosAtivos, StringComparer.OrdinalIgnoreCase);
            var modulos = catalogo
                .Where(s => escolhidos.Contains(s.Codigo))
                .SelectMany(s => s.ModulosExigidos);
            return ModuleDependencies.Close(modulos).ToArray();
        }

        /// <summary>
        /// Valida a lista de servicos pedida para uma loja. Devolve a lista limpa (sem repeticao, na ordem do catalogo)
        /// ou lanca erro de validacao listando o que esta errado.
        /// </summary>
        public static IReadOnlyList<string> ValidarServicosDaLoja(
            IEnumerable<string>? pedidos,
            IReadOnlyCollection<ServicoCatalogoItem> catalogo,
            IReadOnlyCollection<ServicoDoTipo> permitidosDoTipo)
        {
            var erros = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var escolhidos = new HashSet<string>((pedidos ?? Array.Empty<string>()).Select(c => c.Trim().ToLowerInvariant()));
            var permitidos = permitidosDoTipo.Select(p => p.Codigo).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var codigo in escolhidos)
            {
                var item = catalogo.FirstOrDefault(s => s.Codigo == codigo);
                if (item == null || !item.Ativo)
                {
                    Adicionar(erros, "servicos", $"O servico '{codigo}' nao existe no catalogo.");
                }
                else if (!permitidos.Contains(codigo))
                {
                    Adicionar(erros, "servicos", $"O servico '{item.Nome}' nao esta disponivel para este tipo de estabelecimento.");
                }
            }

            if (erros.Count > 0)
            {
                throw new RequestValidationException("Dados invalidos.", erros);
            }

            return catalogo.Where(s => escolhidos.Contains(s.Codigo)).Select(s => s.Codigo).ToList();
        }

        /// <summary>Servicos que um numero pode atender: so os que a loja tem ligados.</summary>
        public static IReadOnlyList<string> ValidarServicosDoCanal(IEnumerable<string>? pedidos, IReadOnlyCollection<string> ativosDaLoja)
        {
            var escolhidos = (pedidos ?? Array.Empty<string>()).Select(c => c.Trim().ToLowerInvariant()).Distinct().ToList();
            var fora = escolhidos.Where(c => !ativosDaLoja.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (fora.Count > 0)
            {
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["servicos"] = new List<string> { $"A loja nao tem estes servicos ligados: {string.Join(", ", fora)}." }
                });
            }

            return escolhidos;
        }

        private static void Adicionar(Dictionary<string, List<string>> erros, string campo, string mensagem)
        {
            if (!erros.TryGetValue(campo, out var lista))
            {
                erros[campo] = lista = new List<string>();
            }

            lista.Add(mensagem);
        }
    }
}
