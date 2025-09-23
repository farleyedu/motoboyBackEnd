// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace APIBack.Automation.Services
{
    public class PromptAssembler
    {
        public string? Assemble((IReadOnlyList<string> Gerais, IReadOnlyList<string> Modulos, IReadOnlyList<string> Estabelecimento) prompts)
        {
            var builder = new StringBuilder();
            AppendSection(builder, prompts.Gerais);
            AppendSection(builder, prompts.Modulos);
            AppendSection(builder, prompts.Estabelecimento);

            var final = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(final) ? null : final;
        }

        private static void AppendSection(StringBuilder builder, IReadOnlyList<string> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            foreach (var entry in entries.Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine().AppendLine();
                }

                builder.Append(entry.Trim());
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
