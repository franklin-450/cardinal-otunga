using System.Threading.Tasks;
using EduTrackTrial.DTOs;

namespace EduTrackTrial.Services
{
    public interface IMpesaDarajaService
    {
        Task<MpesaResultDto> SendStkPushAsync(string phone, int amount, string reference);
    }
}