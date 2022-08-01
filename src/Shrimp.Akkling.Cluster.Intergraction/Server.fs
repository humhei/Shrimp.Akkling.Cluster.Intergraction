namespace Shrimp.Akkling.Cluster.Intergraction

open System.Collections.Generic

#nowarn "0104"
open Akkling
open Akka.Actor
open Akka.Cluster
open Akkling.Cluster
open Newtonsoft.Json.Linq
open System
open Shrimp.Akkling.Cluster.Intergraction.Configuration
open System.Threading

type ServerEndpointsUpdatedEvent<'CallbackMsg> = ServerEndpointsUpdatedEvent of Map<Address, RemoteActor<'CallbackMsg>>


type ServerSender<'Response> internal (sender: IActorRef<'Response>, self, queue: Queue<Guid>) =

    member x.Path = sender.Path

    interface ICanTell<'Response> with
        member this.AskWith(fmsg, ?timespanOp) = raise (new NotImplementedException())

        member x.Ask(message, timeSpan) =
            failwith "Server sender cannot perform an asking "



        member x.Tell(message, _) =
            let message = 
                let count = queue.Count
                if count = 0 
                then box message
                elif count = 1 
                then
                    let token = queue.Dequeue()
                    { Response = message 
                      Guid = token }
                    |> box
                else failwith "Invalid token"

            sender.Underlying.Tell(message, self)

        member x.Underlying = sender.Underlying

type ServerNodeActor<'CallbackMsg, 'ServerMsg> =
    inherit ExtActor<'ServerMsg>
    abstract member Callback: 'CallbackMsg -> unit
    abstract member EndpointsUpdated: Event<ServerEndpointsUpdatedEvent<'CallbackMsg>>
    abstract member Sender: unit -> ServerSender<'Response>
    abstract member Self: unit


[<RequireQualifiedAccess>]
type private ServerNodeMsg =
    | AddClient of RemoteActorIdentity
    | RemoveClient of Address

type private IServerNodeFunActor<'ServerMsg> =
    abstract member Become: Effect<'ServerMsg> -> unit

type private ServerNodeTypedContext<'CallbackMsg, 'ServerMsg, 'Actor when 'Actor :> ActorBase and 'Actor :> IWithUnboundedStash>(context : IActorContext, actor : 'Actor) = 
    inherit TypedContext<'ServerMsg, 'Actor>(context, actor)
    let mutable endpoints: Map<Address, RemoteActor<'CallbackMsg>>  = Map.empty
    let log = context.System.Log

    let serverEndpointsUpdatedEvent = new Event<_>()

    let askingTokenQueue = Queue<_>()

    member x.Sender() =
        let x = x :> Actor<_>
        ServerSender(x.Sender(), untyped x.Self, (askingTokenQueue))

    member x.ProcessException(ex: Exception) =
        let errorMsg = ex.ToString()
        log.Error errorMsg
        let count = askingTokenQueue.Count
        if ex.GetType() = typeof<System.Exception>
        then
            if count = 1
            then x.Sender() <! ErrorResponse.ServerText (errorMsg) 
            elif count = 0 
            then x.Sender() <! ErrorNotifycation.ServerText (errorMsg) 
            else failwith "Invalid token"
        else 
            if count = 1
            then x.Sender() <! ErrorResponse.ServerException (ex, ex.StackTrace) 
            elif count = 0 
            then x.Sender() <! ErrorNotifycation.ServerException (ex, ex.StackTrace) 
            else failwith "Invalid token"


    member x.EnqueueAskingToken(token: Guid) =
        askingTokenQueue.Enqueue(token)

    member x.SetEndpoints(value) = 
        endpoints <- value 
        serverEndpointsUpdatedEvent.Trigger(ServerEndpointsUpdatedEvent endpoints)

    member x.GetEndpoints() = endpoints

    interface ExtActor<'ServerMsg> with 
        member x.Become(effect: _) =
            match box actor with
            | :? IServerNodeFunActor<'ServerMsg> as act -> act.Become effect
            | _ -> raise (Exception("Couldn't use actor in typed context"))

    interface ServerNodeActor<'CallbackMsg, 'ServerMsg> with 

        member x.EndpointsUpdated = serverEndpointsUpdatedEvent 

        member x.Sender() = x.Sender()

        member x.Callback(callbackMsg) =
            let ctx = (x :> ExtActor<_>)
            for endpoint in endpoints do
                (endpoint.Value :> ICanTell<_>).Tell(callbackMsg, untyped ctx.Self)

        member x.Self = 
            failwith "Invalid opertor"
            ()

type private ServerNodeFunActor<'CallbackMsg, 'ServerMsg>(actor: ServerNodeActor<'CallbackMsg, 'ServerMsg> -> Effect<'ServerMsg>) as this =
    inherit Actor()
    let untypedContext = UntypedActor.Context :> IActorContext
    let log = untypedContext.System.Log
    let ctx = ServerNodeTypedContext<'CallbackMsg ,'ServerMsg, ServerNodeFunActor<'CallbackMsg, 'ServerMsg>>(untypedContext, this)
    
    let mutable behavior = 
        try
            actor ctx
        with ex ->
            log.Error(ex.Message)
            raise ex

    let system = untypedContext.System


    member x.EnqueueAskingToken(token) = ctx.EnqueueAskingToken(token)

    member internal x.ProcessServerNodeMsg(serverNodeMsg) =
        match serverNodeMsg with
        | ServerNodeMsg.AddClient (clientIdentity) ->
            match Map.tryFind clientIdentity.Address (ctx.GetEndpoints()) with
            | Some _ -> ()
            | None -> ctx.SetEndpoints(ctx.GetEndpoints().Add(clientIdentity.Address, RemoteActor<_>.Create(system, clientIdentity)))
        | ServerNodeMsg.RemoveClient (address) ->
            match Map.tryFind address (ctx.GetEndpoints()) with 
            | Some clientIdentity -> 
                ctx.SetEndpoints(ctx.GetEndpoints().Remove(address))
            | None -> ()

    member x.GetEndpoints() = ctx.GetEndpoints()

    abstract member Next: current: Effect<'ServerMsg> * context: ServerNodeActor<'CallbackMsg, 'ServerMsg> * message: obj -> Effect<'ServerMsg>

    default x.Next (current : Effect<'ServerMsg>, _ : ServerNodeActor<'CallbackMsg, 'ServerMsg>, message : obj) : Effect<'ServerMsg> = 
        match message with
        | :? ServerNodeMsg as serverNodeMsg ->
            x.ProcessServerNodeMsg(serverNodeMsg)
            current
        | :? LifecycleEvent -> 
            // we don't treat unhandled lifecycle events as casual unhandled messages
            current
        | :? JObject as jobj ->
            match current with
            | :? Become<'ServerMsg> as become -> become.Next <| jobj.ToObject<'ServerMsg>()
            | _ -> current

        | :? 'ServerMsg as msg -> 
            match current with
            | :? Become<'ServerMsg> as become -> become.Next msg
            | _ -> current

        | other -> 
            this.Unhandled other
            current

    abstract member HandleNextBehavior: msg: obj * nextBehavior: Effect<'ServerMsg> -> unit

    default __.HandleNextBehavior(msg, nextBehavior) =
        match msg with 
        | :? 'ServerMsg as msg ->
            match nextBehavior with
            | :? Become<'ServerMsg> -> behavior <- nextBehavior
            | :? AsyncEffect<'ServerMsg> as a ->
                Akka.Dispatch.ActorTaskScheduler.RunTask(System.Func<System.Threading.Tasks.Task>(fun () -> 
                    let task = 
                        async {
                            let! eff = a.Effect
                            match eff with
                            | :? Become<'ServerMsg> -> behavior <- eff
                            | effect -> effect.OnApplied(ctx, msg)
                            () } |> Async.StartAsTask
                    upcast task )
                )

            | effect -> effect.OnApplied(ctx, msg)

        | _ -> ()

    member this.Become(effect) = behavior <- effect

    interface IServerNodeFunActor<'ServerMsg> with 
        member x.Become(effect) = x.Become(effect)

    member this.Handle (msg: obj) = 
        let nextBehavior = 
            try 
                this.Next (behavior, ctx, msg)
            with ex ->
                ctx.ProcessException(ex)

                upcast Ignore

        this.HandleNextBehavior(msg, nextBehavior)
    
    member __.Sender() : IActorRef = base.Sender
    member this.InternalUnhandled(message: obj) : unit = this.Unhandled message
    override this.OnReceive msg = this.Handle msg
    
    override this.PostStop() = 
        base.PostStop()

        let b = system.EventStream.Unsubscribe(this.Self)
        if b then log.Info (sprintf "[SERVER] Actor unsubscribed to endpoint msg: %O" this.Self)
        else log.Error (sprintf "[SERVER] Actor unsubscribed to endpoint msg: %O failed" this.Self)
        
        this.Handle PostStop
    
    override this.PreStart() = 
        base.PreStart()

        let b = system.EventStream.Subscribe(this.Self, typeof<ServerNodeMsg>)
        if b then log.Info (sprintf "[SERVER] Actor subscribed to endpoint msg: %O" this.Self)
        else log.Error (sprintf "[SERVER] Actor subscribed to endpoint msg: %O failed" this.Self)

        this.Handle PreStart
    
    override this.PreRestart(cause, msg) = 
        base.PreRestart(cause, msg)
        this.Handle(PreRestart(cause, msg))
    
    override this.PostRestart(cause) = 
        base.PostRestart cause
        this.Handle(PostRestart cause)




type private ServerEndpointFunActor<'CallbackMsg, 'ServerMsg>(actor: ServerNodeActor<'CallbackMsg, 'ServerMsg> -> Effect<'ServerMsg>, clientRoleName)  =
    inherit ServerNodeFunActor<'CallbackMsg, 'ServerMsg>(actor)
    override x.Next (current : Effect<'ServerMsg>, context : ServerNodeActor<'CallbackMsg, 'ServerMsg>, message : obj) : Effect<'ServerMsg> = 
        
        let actorContext = (context :> Actor<_>)

        let clusterSystem = context.System

        let log = clusterSystem.Log

        let cluster = Cluster.Get(clusterSystem)

        match message with
        | :? EndpointMsg as endpointMsg ->
            match endpointMsg with 
            | EndpointMsg.AddClient clientIdentity ->
                match Map.tryFind clientIdentity.Address (x.GetEndpoints()) with
                | Some _ -> current
                | None ->
                    let addClientMsg = ServerNodeMsg.AddClient clientIdentity
                    clusterSystem.EventStream.Publish(addClientMsg)
                    log.Info (sprintf "[SERVER] Add remote client: %O" clientIdentity.Address)
                    current

            | EndpointMsg.RemoveClient address ->
                match Map.tryFind address (x.GetEndpoints()) with 
                | Some _ -> 
                    clusterSystem.EventStream.Publish(ServerNodeMsg.RemoveClient(address))
                    log.Info (sprintf "[SERVER] Remove Client %O" address)
                    current

                | None -> current

            | _ -> 
                log.Error (sprintf "[SERVER] Unexcepted msg %O" message)
                current

        | LifecycleEvent e ->
            match e with
            | PreStart ->
                cluster.Subscribe(untyped actorContext.Self, ClusterEvent.InitialStateAsEvents,
                    [| typedefof<ClusterEvent.IMemberEvent> |])
                log.Info (sprintf "Actor subscribed to Cluster status updates: %O" actorContext.Self)
                current
            | PostStop ->
                cluster.Unsubscribe(untyped actorContext.Self)
                log.Info (sprintf "Actor unsubscribed from Cluster status updates: %O" actorContext.Self)
                current
            | _ -> current

        | IMemberEvent e ->
            match e with
            | MemberDowned m -> log.Info (sprintf "[SERVER] Node Downed up: %O" m)
            | MemberWeaklyUp m -> 
                log.Info (sprintf "[SERVER] Node Weakly up: %O" m)
                

            | MemberJoined m | MemberUp m  ->
                match e with 
                | MemberJoined _ -> log.Info (sprintf "[SERVER] Node joined: %O" m)
                | MemberUp _ -> log.Info (sprintf "[SERVER] Node up: %O" m)
                | _ -> failwith "Invalid token"

                if m.HasRole clientRoleName then
                    let addClientMsg = ServerNodeMsg.AddClient {Role = clientRoleName; Address = m.Address}
                    clusterSystem.EventStream.Publish(addClientMsg)

            | MemberLeft m ->
                log.Info (sprintf "[SERVER] Node left: %O" m)

            | MemberExited m ->
                log.Info (sprintf "[SERVER] Node exited: %O" m)

            | MemberRemoved m ->
                log.Info (sprintf "[SERVER] Node removed: %O" m)
                if m.HasRole(clientRoleName) then
                    clusterSystem.EventStream.Publish(ServerNodeMsg.RemoveClient m.Address)

            current

        | :? ServerMsgAskingToken<'ServerMsg> as token ->
            log.Info (sprintf "[SERVER] recieve msg %O" token)
            x.EnqueueAskingToken(token.Guid)
            let sender = context.Sender()
            match Map.tryFind sender.Path.Address (x.GetEndpoints()), sender.Path.Name = clientRoleName with 
            | Some _, true -> 
                match current with
                | :? Become<'ServerMsg> as become -> 
                    become.Next token.ServerMsg


                | _ -> current

            | None, true ->
                base.ProcessServerNodeMsg(ServerNodeMsg.AddClient { Address = sender.Path.Address; Role = clientRoleName })
                match current with
                | :? Become<'ServerMsg> as become -> 
                    become.Next token.ServerMsg

                | _ -> current


            | _, false -> 
                log.Error (sprintf "[SERVER] Unknown remote client %O" sender.Path.Address )
                current

        | :? ServerNodeMsg ->
            base.Next (current, context, message)

        | :? 'ServerMsg ->
            base.Next (current, context, message)

        | _ ->
            log.Error (sprintf "[SERVER] Unknown message %O" message )
            base.Next (current, context, message)

    override x.HandleNextBehavior(msg: obj, nextBehavior) =
        match msg with 
        | :? ServerMsgAskingToken<'ServerMsg> as token ->
            base.HandleNextBehavior(token.ServerMsg, nextBehavior)
        | _ -> base.HandleNextBehavior(msg, nextBehavior)
        

[<RequireQualifiedAccess>]
module Server =

    //let nodeProps (receive: ServerNodeActor<'CallbackMsg, 'ServerMsg>->Effect<'ServerMsg>) : Props<'ServerMsg> = 
    //    Props<'ServerMsg>.Create<ServerNodeFunActor<'CallbackMsg, 'ServerMsg>, ServerNodeActor<'CallbackMsg, 'ServerMsg>, 'ServerMsg>(receive)

    let internal endpointProps clientRoleName (receive: ServerNodeActor<'CallbackMsg, 'ServerMsg>->Effect<'ServerMsg>) : Props<'ServerMsg> = 
        Props<'ServerMsg>.ArgsCreate<ServerEndpointFunActor<'CallbackMsg, 'ServerMsg>, ServerNodeActor<'CallbackMsg, 'ServerMsg>, 'ServerMsg>([| receive; clientRoleName |])




type Server<'CallbackMsg, 'ServerMsg>
    ( systemName, 
      name, 
      clientRoleName: string, 
      remotePort, seedPort, 
      setParams, 
      receive: ServerNodeActor<'CallbackMsg, 'ServerMsg> -> Effect<'ServerMsg> ) =

    let config = 
        Configuration.createClusterConfig [name] systemName remotePort seedPort setParams

    let clusterSystem = System.create systemName config

    let log = clusterSystem.Log

    let actor = spawn clusterSystem name (Server.endpointProps clientRoleName (receive))

    member x.SystemName = systemName

    member x.Config = config

    member x.ClusterSystem = clusterSystem

    member x.Log = log
