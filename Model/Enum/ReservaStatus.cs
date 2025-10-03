using NpgsqlTypes;

namespace APIBack.Model
{
    public enum ReservaStatus
    {
        [PgName("pendente")]
        Pendente,

        [PgName("confirmado")]
        Confirmado,

        [PgName("cancelado")]
        Cancelado,

        [PgName("concluido")]
        Concluido
    }
}