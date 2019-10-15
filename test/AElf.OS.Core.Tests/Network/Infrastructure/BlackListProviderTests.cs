using System;
using System.Net;
using System.Threading.Tasks;
using AElf.OS.Network.Helpers;
using AElf.OS.Network.Infrastructure;
using Shouldly;
using Xunit;

namespace AElf.OS.Network
{
    public class BlackListProviderTests : NetworkInfrastructureTestBase
    {
        private IBlackListedPeerProvider _blackListProvider;

        public BlackListProviderTests()
        {
            _blackListProvider = GetRequiredService<IBlackListedPeerProvider>();
        }

        [Fact]
        public async Task ShouldAddTo()
        {
            var ipAddress = IPAddress.Parse("127.0.0.1:5000");

            _blackListProvider.AddIpToBlackList(ipAddress);
            _blackListProvider.IsIpBlackListed(ipAddress).ShouldBeTrue();
            
            await Task.Delay(TimeSpan.FromSeconds(2));
            
            _blackListProvider.IsIpBlackListed(ipAddress).ShouldBeFalse();
        }
    }
}