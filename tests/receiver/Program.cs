using receiver;
using Arbitrer;
using MediatR;
using cqrs.models.Commands;
using System.Reflection;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
      services.AddMediatR(Assembly.GetExecutingAssembly());
      services.AddArbitrer(opt =>
      {
        opt.Behaviour = ArbitrerBehaviourEnum.Explicit;
        opt.SetAsLocalRequest<MediatRRequest1>();
        opt.SetAsLocalRequest<MediatRRequest2>();
        opt.SetAsLocalRequest<MediatRRequest3>();
        opt.SetAsLocalRequest<MediatRRequest4>();
        opt.SetAsLocalRequest<MediatRRequest5>();
        opt.SetAsLocalRequest<MediatRRequestWithException>();
        opt.SetAsLocalRequest<MediatRRequestWithHandlerException>();
        opt.ListenForNotification<MediatorNotification1>();
      });
      services.AddArbitrerRabbitMQMessageDispatcher(opt => context.Configuration.GetSection("rabbitmq").Bind(opt));
      services.ResolveArbitrerCalls();

    })
    .Build();

await host.RunAsync();
