using Autofac;
using Squid.Core.Services.DataSeeding;

namespace Squid.UnitTests.Services.DataSeeding;

public class DataSeederRunnerTests
{
    [Fact]
    public void Seeders_RunInOrderProperty()
    {
        var executionOrder = new List<int>();

        var seeder1 = CreateSeeder(300, () => executionOrder.Add(300));
        var seeder2 = CreateSeeder(100, () => executionOrder.Add(100));
        var seeder3 = CreateSeeder(200, () => executionOrder.Add(200));

        var runner = BuildRunner(seeder2, seeder1, seeder3);

        runner.Start();

        executionOrder.ShouldBe(new List<int> { 100, 200, 300 });
    }

    [Fact]
    public void Seeder_Failure_DoesNotPreventSubsequentSeeders()
    {
        var executionOrder = new List<int>();

        var failingSeeder = CreateSeeder(100, () => throw new InvalidOperationException("Seeder failed"));
        var successSeeder = CreateSeeder(200, () => executionOrder.Add(200));

        var runner = BuildRunner(failingSeeder, successSeeder);

        runner.Start();

        executionOrder.ShouldBe(new List<int> { 200 });
    }

    [Fact]
    public void NoSeeders_CompletesWithoutError()
    {
        var runner = BuildRunner();

        Should.NotThrow(() => runner.Start());
    }

    private static DataSeederRunner BuildRunner(params IDataSeeder[] seeders)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance<IEnumerable<IDataSeeder>>(seeders);
        var container = builder.Build();

        return new DataSeederRunner(container);
    }

    private static IDataSeeder CreateSeeder(int order, Action action)
    {
        var mock = new Mock<IDataSeeder>();
        mock.SetupGet(s => s.Order).Returns(order);
        mock.Setup(s => s.SeedAsync(It.IsAny<ILifetimeScope>())).Returns(() => { action(); return Task.CompletedTask; });

        return mock.Object;
    }
}
