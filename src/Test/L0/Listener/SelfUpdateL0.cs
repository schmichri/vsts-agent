using Microsoft.VisualStudio.Services.Agent.Listener;
using Xunit;

namespace Microsoft.VisualStudio.Services.Agent.Tests.Listener
{
    public sealed class SelfUpdateL0
    {
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        public void GeneratedScript()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                SelfUpdater updater = new SelfUpdater();
                updater.GenerateBatchScript();
            }
        }
    }
}
