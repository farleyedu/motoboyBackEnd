using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.Model.Gestao
{
    /// <summary>
    /// Dependencias entre modulos: escolher um obriga escolher os que ele exige (fechamento
    /// transitivo). Aplicado no backend ao salvar o estabelecimento, para a API nunca gravar um
    /// pacote inconsistente. As chaves usam o formato do banco (modulo_enum, MAIUSCULAS).
    /// O front espelha esta tabela em moduleDependencies.ts (mantenha as duas iguais).
    /// Modulos futuros (ATENDIMENTO_PEDIDO, AVISOS_RASTREIO, IA_DELIVERY) entram aqui quando
    /// existirem no enum, junto da fase que os cria.
    /// </summary>
    internal static class ModuleDependencies
    {
        private static readonly IReadOnlyDictionary<string, string[]> Requires =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["DELIVERY"] = new[] { "PEDIDOS" },
                ["CARDAPIOWEB"] = new[] { "CARDAPIO", "PEDIDOS" },
            };

        /// <summary>Modulos exigidos diretamente por <paramref name="module"/> (vazio se nenhum).</summary>
        public static IReadOnlyList<string> DirectlyRequiredBy(string module) =>
            Requires.TryGetValue(module, out var required) ? required : Array.Empty<string>();

        /// <summary>Devolve o conjunto original acrescido de tudo que ele exige, transitivamente.</summary>
        public static HashSet<string> Close(IEnumerable<string> modules)
        {
            var result = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(result);
            while (pending.Count > 0)
            {
                foreach (var required in DirectlyRequiredBy(pending.Dequeue()))
                {
                    if (result.Add(required))
                    {
                        pending.Enqueue(required);
                    }
                }
            }
            return result;
        }

        /// <summary>Quem exige <paramref name="module"/> entre os modulos escolhidos (para explicar o bloqueio).</summary>
        public static IReadOnlyList<string> RequiredBy(string module, IEnumerable<string> chosen) =>
            chosen.Where(other => DirectlyRequiredBy(other).Contains(module, StringComparer.OrdinalIgnoreCase)).ToList();
    }
}
