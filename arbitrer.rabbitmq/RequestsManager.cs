using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Arbitrer.RabbitMQ
{
  public class RequestsManager : IHostedService
  {
    private readonly ILogger<MessageDispatcher> logger;
    private readonly IArbitrer arbitrer;
    private readonly IServiceProvider provider;

    private IConnection _connection = null;
    private IModel _channel = null;
    private List<string> _subscriptions = new List<string>();

    private readonly MessageDispatcherOptions options;

    public RequestsManager(IOptions<MessageDispatcherOptions> options, ILogger<MessageDispatcher> logger, IArbitrer arbitrer, IServiceProvider provider)
    {
      this.options = options.Value;
      this.logger = logger;
      this.arbitrer = arbitrer;
      this.provider = provider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      if (_connection == null)
      {
        logger.LogInformation($"ARBITRER: Creating RabbitMQ Conection to '{options.HostName}'...");
        var factory = new ConnectionFactory
        {
          HostName = options.HostName,
          UserName = options.UserName,
          Password = options.Password,
          VirtualHost = options.VirtualHost,
          Port = options.Port,
          DispatchConsumersAsync = true,
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.ExchangeDeclare(Consts.ArbitrerExchangeName, ExchangeType.Topic);

        logger.LogInformation($"ARBITRER: ready !");
      }


      foreach (var t in arbitrer.GetLocalRequestsTypes())
      {

        _channel.QueueDeclare(queue: t.FullName.Replace(".", "_"), durable: false, exclusive: false, autoDelete: false, arguments: null);
        _channel.QueueBind(t.FullName.Replace(".", "_"), Consts.ArbitrerExchangeName, t.FullName.Replace(".", "_"));


        var consumer = new AsyncEventingBasicConsumer(_channel);

        var consumermethod = typeof(RequestsManager).GetMethod("ConsumeChannelMessage", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(t);

        consumer.Received += async (s, ea) => await (Task)consumermethod.Invoke(this, new object[] { s, ea });
        _channel.BasicConsume(queue: t.FullName.Replace(".", "_"), autoAck: false, consumer: consumer);
      }
      _channel.BasicQos(0, 1, false);
      return Task.CompletedTask;
    }

    private async Task ConsumeChannelMessage<T>(object sender, BasicDeliverEventArgs ea)
    {
      var msg = ea.Body.ToArray();
      logger.LogDebug("Elaborating message : {0}", Encoding.UTF8.GetString(msg));
      var message = JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(msg), options.SerializerSettings);

      var replyProps = _channel.CreateBasicProperties();
      replyProps.CorrelationId = ea.BasicProperties.CorrelationId;

      var mediator = provider.CreateScope().ServiceProvider.GetRequiredService<IMediator>();
      string responseMsg = null;
      try
      {
        var response = await mediator.Send(message);
        responseMsg = JsonConvert.SerializeObject(new Messages.ResponseMessage { Content = response, Status = Messages.StatusEnum.Ok }, options.SerializerSettings);
        logger.LogDebug("Elaborating sending response : {0}", responseMsg);
      }
      catch (Exception ex)
      {
        responseMsg = JsonConvert.SerializeObject(new Messages.ResponseMessage { Exception = ex, Status = Messages.StatusEnum.Exception }, options.SerializerSettings);
        logger.LogError(ex, $"Error executing message of type {typeof(T)} from external service");

      }
      finally
      {
        _channel.BasicPublish(exchange: "", routingKey: ea.BasicProperties.ReplyTo, basicProperties: replyProps, body: Encoding.UTF8.GetBytes(responseMsg ?? ""));
        _channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
      }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
      foreach (var s in _subscriptions)
        _channel.BasicCancel(s);

      try { _channel?.Close(); } catch { }
      try { _connection?.Close(); } catch { }
      return Task.CompletedTask;
    }
  }
}