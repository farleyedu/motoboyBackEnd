using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace APIBack.Service
{
    public sealed record CrmEmpresaExistente(Guid Id, string NomeFantasia, string? Cnpj);

    public sealed record CrmCandidatoAssociacao(Guid EmpresaId, string Nome, string? Cnpj, string Motivo);

    /// <summary>
    /// Com que empresa ja cadastrada o fechamento de uma oportunidade pode se ligar. Nada e ligado sozinho: o servidor so sugere
    /// candidatos e o operador confirma qual e. CNPJ igual e a mesma empresa (o operador tem que confirmar essa, nao criar outra);
    /// nome parecido so aparece como sugestao. Nome identico nao liga nada.
    /// </summary>
    public static class CrmAssociacaoRegras
    {
        public const string MotivoCnpj = "cnpj";
        public const string MotivoNomeParecido = "nome_parecido";

        private static readonly HashSet<string> PalavrasIgnoradas = new(StringComparer.Ordinal)
        {
            "ltda", "me", "mei", "epp", "eireli", "sa", "s", "a", "cia", "comercio", "de", "da", "do", "das", "dos", "e"
        };

        public static IReadOnlyList<CrmCandidatoAssociacao> Candidatos(string? cnpj, string nomeEmpresa, IEnumerable<CrmEmpresaExistente> empresas)
        {
            var cnpjDigitos = Digitos(cnpj);
            var nome = NomeComparavel(nomeEmpresa);
            var resultado = new List<CrmCandidatoAssociacao>();

            foreach (var empresa in empresas)
            {
                if (cnpjDigitos != null && Digitos(empresa.Cnpj) == cnpjDigitos)
                {
                    resultado.Add(new CrmCandidatoAssociacao(empresa.Id, empresa.NomeFantasia, empresa.Cnpj, MotivoCnpj));
                }
                else if (NomesParecidos(nome, NomeComparavel(empresa.NomeFantasia)))
                {
                    resultado.Add(new CrmCandidatoAssociacao(empresa.Id, empresa.NomeFantasia, empresa.Cnpj, MotivoNomeParecido));
                }
            }

            return resultado
                .OrderBy(item => item.Motivo == MotivoCnpj ? 0 : 1)
                .ThenBy(item => item.Nome, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Confere a escolha do operador. Devolve a mensagem de erro, ou nulo quando a escolha vale.
        /// Sem empresa escolhida = criar empresa e loja novas, o que so vale quando nenhuma empresa tem o mesmo CNPJ.
        /// </summary>
        public static string? ValidarEscolha(IReadOnlyList<CrmCandidatoAssociacao> candidatos, Guid? empresaEscolhida)
        {
            var mesmoCnpj = candidatos.FirstOrDefault(item => item.Motivo == MotivoCnpj);
            if (!empresaEscolhida.HasValue)
            {
                return mesmoCnpj == null
                    ? null
                    : $"Ja existe a empresa {mesmoCnpj.Nome} com este CNPJ. Confirme que e ela para ligar o contrato.";
            }

            if (mesmoCnpj != null && mesmoCnpj.EmpresaId != empresaEscolhida.Value)
            {
                return $"O CNPJ informado pertence a {mesmoCnpj.Nome}. Escolha essa empresa ou corrija o CNPJ.";
            }

            return null;
        }

        public static bool NomesParecidos(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0)
            {
                return false;
            }

            if (a == b)
            {
                return true;
            }

            var menor = a.Length <= b.Length ? a : b;
            var maior = a.Length <= b.Length ? b : a;
            if (menor.Length >= 4 && maior.Contains(menor, StringComparison.Ordinal))
            {
                return true;
            }

            var limite = Math.Max(1, maior.Length / 5);
            return Math.Abs(a.Length - b.Length) <= limite && Distancia(a, b) <= limite;
        }

        /// <summary>Minusculo, sem acento, sem pontuacao e sem palavras de razao social (ltda, me...), tudo junto.</summary>
        public static string NomeComparavel(string? nome)
        {
            if (string.IsNullOrWhiteSpace(nome))
            {
                return string.Empty;
            }

            var semAcento = new StringBuilder();
            foreach (var ch in nome.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                semAcento.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            }

            var palavras = semAcento.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(palavra => !PalavrasIgnoradas.Contains(palavra));
            return string.Concat(palavras);
        }

        private static string? Digitos(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor))
            {
                return null;
            }

            var digitos = new string(valor.Where(char.IsDigit).ToArray());
            return digitos.Length == 0 ? null : digitos;
        }

        private static int Distancia(string a, string b)
        {
            var anterior = new int[b.Length + 1];
            var atual = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                anterior[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                atual[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var custo = a[i - 1] == b[j - 1] ? 0 : 1;
                    atual[j] = Math.Min(Math.Min(atual[j - 1] + 1, anterior[j] + 1), anterior[j - 1] + custo);
                }

                (anterior, atual) = (atual, anterior);
            }

            return anterior[b.Length];
        }
    }
}
