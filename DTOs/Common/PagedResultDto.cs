using System.Collections.Generic;

namespace APIBack.DTOs.Common
{
    public class PagedResultDto<T>
    {
        public IReadOnlyCollection<T> Itens { get; set; } = new List<T>();
        public int Total { get; set; }
    }
}
