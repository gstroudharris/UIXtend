using System.Threading.Tasks;

namespace UIXtend.Core.Interfaces
{
    public interface IRegionSelectionService : IService
    {
        Task<Windows.Foundation.Rect?> StartSelectionAsync();
    }
}
