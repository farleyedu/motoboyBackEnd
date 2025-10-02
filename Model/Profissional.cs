using System;

namespace APIBack.Model
{
    public class Profissional
    {
        public long Id { get; set; }
        public long IdUsuario { get; set; }
        public long IdEstabelecimento { get; set; }
        public bool Ativo { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }
}
