using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using EduTrackTrial.DTOs;

namespace EduTrackTrial.Services
{
    public class SimulatedMpesaDarajaService : IMpesaDarajaService
    {
        private readonly ILogger<SimulatedMpesaDarajaService> _logger;

        public SimulatedMpesaDarajaService(ILogger<SimulatedMpesaDarajaService> logger)
        {
            _logger = logger;
        }

        public async Task<MpesaResultDto> SendStkPushAsync(string phone, int amount, string reference)
        {
            _logger.LogInformation("Simulated STK push: Phone={Phone}, Amount={Amount}, Ref={Ref}", phone, amount, reference);
            await Task.Delay(300);
            return new MpesaResultDto { Success = true, Message = "Simulated STK pushed" };
        }
    }
}