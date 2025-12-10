using System;
using System.Collections.Generic;
using APIBack.Automation.Models;

namespace APIBack.Automation.Validators
{
    public class EstabelecimentoSelectionValidator
    {
        public UsuarioTenant EnsureUsuarioValido(UsuarioTenant? usuario)
        {
            if (usuario == null || !usuario.IsAtivo)
            {
                throw new UnauthorizedAccessException("Usuário não encontrado ou inativo.");
            }

            return usuario;
        }

        public EstabelecimentoDetalhe EnsureEstabelecimentoSelecionavel(EstabelecimentoDetalhe? estabelecimento)
        {
            if (estabelecimento == null)
            {
                throw new KeyNotFoundException("Estabelecimento não encontrado.");
            }

            var statusAtivo = string.Equals(estabelecimento.Status, "ativo", StringComparison.OrdinalIgnoreCase);
            var flagAtivo = estabelecimento.Ativo != false;

            if (!statusAtivo || !flagAtivo)
            {
                throw new InvalidOperationException("Estabelecimento inativo ou suspenso.");
            }

            return estabelecimento;
        }

        public void EnsureVinculoPermitido(UsuarioEstabelecimentoAcesso? vinculo, bool isSuperAdmin)
        {
            if (isSuperAdmin)
            {
                return;
            }

            if (vinculo == null)
            {
                throw new UnauthorizedAccessException("Usuário não possui vínculo com este estabelecimento.");
            }

            var statusAtivo = string.Equals(vinculo.Status, "ativo", StringComparison.OrdinalIgnoreCase);
            if (vinculo.VinculoAtivo != true || !statusAtivo)
            {
                throw new UnauthorizedAccessException("Vínculo inativo ou pendente.");
            }
        }

        public string NormalizarStatusEstabelecimento(string? status, bool? ativo)
        {
            if (!string.IsNullOrWhiteSpace(status))
            {
                return status;
            }

            return ativo == false ? "inativo" : "ativo";
        }
    }
}
