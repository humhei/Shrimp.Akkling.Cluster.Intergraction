namespace Shrimp.Akkling.Cluster.Intergraction
open Akkling
open Akka.Actor
open Akka.Cluster
open Akkling.Cluster
open Newtonsoft.Json.Linq
open System
open Shrimp.Akkling.Cluster.Intergraction.Configuration
open System.Threading

type ServerEndpointsUpdatedEvent<'CallbackMsg> = ServerEndpointsUpdatedEvent of Map<Address, RemoteActor<'CallbackMsg>>


type ServerNodeActor<'CallbackMsg, 'ServerMsg> =
    inherit ExtActor<'ServerMsg>
    abstract member RespondSafely: responseBuilder: (unit -> obj) -> unit
    abstract member RespondSafely: model: 'model * responseBuilder: (unit -> obj * 'model) -> 'model
    abstract member NotifySafely: processMessage: (unit -> unit) -> unit
    abstract member NotifySafely: model: 'model * processMessage: (unit -> 'model) -> 'model
    abstract member Callback: 'CallbackMsg -> unit
    abstract member EndpointsUpdated: Event<ServerEndpointsUpdatedEvent<'CallbackMsg>>
    abstract member ClientJoinedManualReset: ManualResetEventSlim


[<RequireQualifiedAccess>]
type private ServerNodeMsg =
    | AddClient of RemoteActorIdentity
    | RemoveClient of Address



type private ServerNodeTypedContext<'CallbackMsg, 'ServerMsg, 'Actor when 'Actor :> ActorBase and 'Actor :> IWithUnboundedStash>(context : IActorContext, actor : 'Actor) = 
    inherit TypedContext<'ServerMsg, 'Actor>(context, actor)
    let mutable endpoints: Map<Address, RemoteActor<'CallbackMsg>>  = Map.empty
    let serverEndpointsUpdatedEvent = new Event<_>()
    let clientJoinedManualReset = new ManualResetEventSlim(false)


    member x.SetEndpoints(value) = 
        endpoints <- value 

        if value.Count > 0 && not clientJoinedManualReset.IsSet
        then clientJoinedManualReset.Set()

        serverEndpointsUpdatedEvent.Trigger(ServerEndpointsUpdatedEvent endpoints)

    member x.GetEndpoints() = endpoints

    interface ServerNodeActor<'CallbackMsg, 'ServerMsg> with 

        member x.EndpointsUpdated = serverEndpointsUpdatedEvent 

        member x.ClientJoinedManualReset = clientJoinedManualReset

        member x.RespondSafely(responseBuilder: unit -> obj) =
            let ctx = (x :> ExtActor<_>)

            try 
                let response = responseBuilder()
                (ctx.Sender()).Tell(response, untyped ctx.Self)

            with ex ->
                let errorMsg = ex.ToString()
                ctx.Log.Value.Error(errorMsg)
                if ex.GetType() = typeof<System.Exception>
                then
                    (ctx.Sender()).Tell(ErrorResponse.ServerText errorMsg, untyped ctx.Self)

                else (ctx.Sender()).Tell(ErrorResponse.ServerException ex, untyped ctx.Self)


        member x.RespondSafely(model, responseBuilder) =
            let ctx = (x :> ExtActor<_>)

            try 
                let response, newModel = responseBuilder()
                (ctx.Sender()).Tell(response, untyped ctx.Self)
                newModel
            with ex ->
                let errorMsg = ex.ToString()
                ctx.Log.Value.Error(errorMsg)
                if ex.GetType() = typeof<System.Exception>
                then
                    (ctx.Sender()).Tell(ErrorResponse.ServerText errorMsg, untyped ctx.Self)

                else (ctx.Sender()).Tell(ErrorResponse.ServerException ex, untyped ctx.Self)

                model


        member x.NotifySafely(processMessage) =
            let ctx = (x :> ExtActor<_>)
            try 
                processMessage()
            with ex ->
                let errorMsg = ex.ToString()
                ctx.Log.Value.Error(errorMsg)

                if ex.GetType() = typeof<System.Exception>
                then
                    (ctx.Sender()).Tell(ErrorNotifycation.ServerText errorMsg, untyped ctx.Self)

                else (ctx.Sender()).Tell(ErrorNotifycation.ServerException ex, untyped ctx.Self)

        member x.NotifySafely(model, processMessage) =
            let ctx = (x :> ExtActor<_>)
            try 
                processMessage()
            with ex ->
                let errorMsg = ex.ToString()
                ctx.Log.Value.Error(errorMsg)
                
                if ex.GetType() = typeof<System.Exception>
                then
                    (ctx.Sender()).Tell(ErrorNotifycation.ServerText errorMsg, untyped ctx.Self)

                else (ctx.Sender()).Tell(ErrorNotifycation.ServerException ex, untyped ctx.Self)

                model

        member x.Callback(callbackMsg) =
            let ctx = (x :> ExtActor<_>)
            for endpoint in endpoints do
                (endpoint.Value :> ICanTell<_>).Tell(callbackMsg, untyped ctx.Self)


type private ServerNodeFunActor<'CallbackMsg, 'ServerMsg>(actor: ServerNodeActor<'CallbackMsg, 'ServerMsg> -> Effect<'ServerMsg>) as this =
    inherit Actor()
    let untypedContext = UntypedActor.Context :> IActorContext
    let ctx = ServerNodeTypedContext<'CallbackMsg ,'ServerMsg, ServerNodeFunActor<'CallbackMsg, 'ServerMsg>>(untypedContext, this)
    let mutable behavior = actor ctx
    let log = untypedContext.System.Log
    let system = untypedContext.System

    member x.GetEndpoints() = ctx.GetEndpoints()

    abstract member Next: current: Effect<'ServerMsg> * context: ServerNodeActor<'CallbackMsg, 'ServerMsg> * message: obj -> Effect<'ServerMsg>

    default __.Next (current : Effect<'ServerMsg>, _ : ServerNodeActor<'CallbackMsg, 'ServerMsg>, message : obj) : Effect<'ServerMsg> = 
        match message with
        | :? ServerNodeMsg as serverNodeMsg ->
            match serverNodeMsg with
            | ServerNodeMsg.AddClient (clientIdentity) ->
                match Map.tryFind clientIdentity.Address (ctx.GetEndpoints()) with
                | Some _ -> current
                | None ->
                    ctx.SetEndpoints(ctx.GetEndpoints().Add(clientIdentity.Address, RemoteActor<_>.Create(system, clientIdentity)))
                    current
            | ServerNodeMsg.RemoveClient (address) ->
                match Map.tryFind address (ctx.GetEndpoints()) with 
                | Some clientIdentity -> 
                    ctx.SetEndpoints(ctx.GetEndpoints().Remove(address))
                    current
                | None -> current

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
        match nextBehavior with
        | :? Become<'ServerMsg> -> behavior <- nextBehavior
        | :? AsyncEffect<'ServerMsg> as a ->
            Akka.Dispatch.ActorTaskScheduler.RunTask(System.Func<System.Threading.Tasks.Task>(fun () -> 
                let task = 
                    async {
                        let! eff = a.Effect
                        match eff with
                        | :? Become<'ServerMsg> -> behavior <- eff
                        | effect -> effect.OnApplied(ctx, msg :?> 'ServerMsg)
                        () } |> Async.StartAsTask
                upcast task ))

        | effect -> effect.OnApplied(ctx, msg :?> 'ServerMsg)


    member this.Handle (msg: obj) = 
        let nextBehavior = this.Next (behavior, ctx, msg)
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



type private ServerMsgWrapper<'ServerMsg> = ServeerMsgWrapper of 'ServerMsg

type private ServerEndpointFunActor<'CallbackMsg, 'ServerMsg>(actor: ServerNodeActor<'CallbackMsg, 'ServerMsg> -> Effect<'ServerMsg>, clientRoleName)  =
    inherit ServerNodeFunActor<'CallbackMsg, 'ServerMsg>(actor)
    override x.Next (current : Effect<'ServerMsg>, context : ServerNodeActor<'CallbackMsg, 'ServerMsg>, message : obj) : Effect<'ServerMsg> = 
        
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
                cluster.Subscribe(untyped context.Self, ClusterEvent.InitialStateAsEvents,
                    [| typedefof<ClusterEvent.IMemberEvent> |])
                log.Info (sprintf "Actor subscribed to Cluster status updates: %O" context.Self)
                current
            | PostStop ->
                cluster.Unsubscribe(untyped context.Self)
                log.Info (sprintf "Actor unsubscribed from Cluster status updates: %O" context.Self)
                current
            | _ -> current

        | IMemberEvent e ->
            match e with
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

        | :? 'ServerMsg as serverMsg ->
            log.Info (sprintf "[SERVER] recieve msg %O" serverMsg)
            let sender = context.Sender()

            match Map.tryFind sender.Path.Address (x.GetEndpoints()), sender.Path.Name = clientRoleName with 
            | Some _, true -> 
                base.Self.Forward(ServeerMsgWrapper serverMsg)
                current


            | None, true ->
                clusterSystem.EventStream.Publish(ServerNodeMsg.AddClient { Address = sender.Path.Address; Role = clientRoleName }) 
                base.Self.Forward(ServeerMsgWrapper serverMsg)
                current

            | _, false -> 
                log.Error (sprintf "[SERVER] Unknown remote client %O" sender.Path.Address )
                current

        | :? ServerNodeMsg ->
            base.Next (current, context, message)

        | :? ServerMsgWrapper<'ServerMsg> as keep ->
            match current with
            | :? Become<'ServerMsg> as become -> 
                let (ServeerMsgWrapper serverMsg) = keep
                become.Next serverMsg
            | _ -> current
        | _ ->
            log.Error (sprintf "[SERVER] Unknown message %O" message )
            base.Next (current, context, message)

    override x.HandleNextBehavior(msg: obj, nextBehavior) =
        match msg with 
        | :? ServerMsgWrapper<'ServerMsg> as keep ->
            let (ServeerMsgWrapper msg) = keep 
            base.HandleNextBehavior(msg, nextBehavior)
        | _ -> base.HandleNextBehavior(msg, nextBehavior)
        

[<RequireQualifiedAccess>]
module Server =
    let nodeProps (receive: ServerNodeActor<'CallbackMsg, 'ServerMsg>->Effect<'ServerMsg>) : Props<'ServerMsg> = 
        Props<'ServerMsg>.Create<ServerNodeFunActor<'CallbackMsg, 'ServerMsg>, ServerNodeActor<'CallbackMsg, 'ServerMsg>, 'ServerMsg>(receive)

    let internal endpointProps clientRoleName (receive: ServerNodeActor<'CallbackMsg, 'ServerMsg>->Effect<'ServerMsg>) : Props<'ServerMsg> = 
        Props<'ServerMsg>.ArgsCreate<ServerEndpointFunActor<'CallbackMsg, 'ServerMsg>, ServerNodeActor<'CallbackMsg, 'ServerMsg>, 'ServerMsg>([| receive; clientRoleName |])




type Server<'CallbackMsg, 'ServerMsg>
    ( systemName, 
      name, 
      clientRoleName: string, 
      remotePort, seedPort, 
      setParams, 
      receive: ServerNodeActor<'CallbackMsg, 'ServerMsg> -> Effect<'ServerMsg> ) =

    let config = Configuration.createClusterConfig [name] systemName remotePort seedPort setParams
    
    let clusterSystem = System.create systemName config

    let log = clusterSystem.Log

    let actor = spawn clusterSystem name (Server.endpointProps clientRoleName (receive))

    member x.SystemName = systemName

    member x.Config = config

    member x.ClusterSystem = clusterSystem

    member x.Log = log
