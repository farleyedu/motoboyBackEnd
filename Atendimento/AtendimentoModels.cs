using System;
using System.Collections.Generic;

namespace APIBack.Atendimento
{
    /// <summary>Servico do catalogo (o que o cliente final pode fazer). Os modulos exigidos sao valores de modulo_enum.</summary>
    public sealed record ServicoCatalogoItem(
        string Codigo, string Nome, string? Descricao, IReadOnlyList<string> ModulosExigidos, int Ordem, bool Ativo);

    /// <summary>Servico que um tipo de estabelecimento permite e se ele nasce ligado.</summary>
    public sealed record ServicoDoTipo(string Codigo, bool Padrao);

    /// <summary>Servico ligado (ou desligado) numa loja.</summary>
    public sealed class ServicoDaLoja
    {
        public string Codigo { get; set; } = string.Empty;
        public bool Ativo { get; set; }
        public string ConfigJson { get; set; } = "{}";
    }

    public static class ModoAtendimento
    {
        public const string Bot = "bot";
        public const string Humano = "humano";
        public const string Hibrido = "hibrido";

        public static readonly IReadOnlyList<string> Todos = new[] { Bot, Humano, Hibrido };

        public static bool Valido(string? modo) => modo != null && ((IList<string>)Todos).Contains(modo);
    }

    public static class StatusCanal
    {
        public const string Configurando = "configurando";
        public const string Ativo = "ativo";
        public const string Erro = "erro";
        public const string Inativo = "inativo";
    }

    /// <summary>Numero de WhatsApp de uma loja. O token nunca sai deste objeto em texto puro: so se sabe se existe.</summary>
    public sealed class CanalWhatsapp
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        /// <summary>ID do numero na Meta (WhatsApp Business Manager), usado para enviar. Nao e o telefone.</summary>
        public string PhoneNumberId { get; set; } = string.Empty;
        /// <summary>Telefone que o cliente enxerga, normalizado: +DDI DDD numero.</summary>
        public string NumeroE164 { get; set; } = string.Empty;
        public string? Nome { get; set; }
        public string? TokenCifrado { get; set; }
        public bool TemToken => !string.IsNullOrWhiteSpace(TokenCifrado);
        public string Status { get; set; } = StatusCanal.Configurando;
        public string ModoAtendimento { get; set; } = Atendimento.ModoAtendimento.Hibrido;
        public string ConfigJson { get; set; } = "{}";
        public DateTime? VerificadoEm { get; set; }
        public DateTime? UltimoRecebimentoEm { get; set; }
        public DateTime? UltimoEnvioOkEm { get; set; }
        public string? UltimoErro { get; set; }
        public DateTime? UltimoErroEm { get; set; }
        public List<string> Servicos { get; set; } = new();
    }

    /// <summary>Quem esta pedindo a operacao. "Gestao" e o super admin: so ele cria canais e liga servicos.</summary>
    public sealed record AtendimentoAtor(int UsuarioId, bool Gestao, Guid? EstabelecimentoId, Func<string, string, bool> TemPermissao);

    /// <summary>Violacao de unicidade: o numero (ou o ID da Meta) ja pertence a outra loja.</summary>
    public sealed class CanalConflitoException : Exception
    {
        public CanalConflitoException(string campo, string message) : base(message) => Campo = campo;
        public string Campo { get; }
    }
}
