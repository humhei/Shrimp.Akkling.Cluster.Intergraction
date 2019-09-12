namespace Shrimp.Akkling.Cluster.Intergraction
open Akkling
open Akka.Actor
open Akka.Cluster
open Akkling.Cluster
open Extensions
open Newtonsoft.Json.Linq
open System



[<RequireQualifiedAccess>]
type ServerNodeMsg =
    | AddClient of RemoteActorIdentity
    | RemoveClient of Address


type ServerNodeActor<'CallbackMsg, 'ServerMsg> =
    inherit ExtActor<'ServerMsg>
    abstract member Endpoints: Map<Address, RemoteActor<'CallbackMsg>> 


type ServerNodeTypedContext<'CallbackMsg, 'ServerMsg, 'Actor when 'Actor :> ActorBase and 'Actor :> IWithUnboundedStash>(context : IActorContext, actor : 'Actor) = 
    inherit TypedContext<'ServerMsg, 'Actor>(context, actor)
    let mutable endpoints = Map.empty

    member internal x.SetEndpoints(value) = endpoints <- value 

    member x.Endpoints = endpoints

    interface ServerNodeActor<'CallbackMsg, 'ServerMsg> with 
        member x.Endpoints = endpoints

type ServerNodeFunActor<'CallbackMsg, 'ServerMsg>(actor: ServerNodeActor<'CallbackMsg, 'ServerMsg> -> Effect<'ServerMsg>) as this =
    inherit Actor()
    let untypedContext = UntypedActor.Context :> IActorContext
    let ctx = ServerNodeTypedContext<'CallbackMsg ,'ServerMsg, ServerNodeFunActor<'CallbackMsg, 'ServerMsg>>(untypedContext, this)
    let mutable behavior = actor ctx
    let log = untypedContext.System.Log
    let system = untypedContext.System

    abstract member Next: current: Effect<'ServerMsg> * context: ServerNodeActor<'CallbackMsg, 'ServerMsg> * message: obj -> Effect<'ServerMsg>

    default __.Next (current : Effect<'ServerMsg>, _ : ServerNodeActor<'CallbackMsg, 'ServerMsg>, message : obj) : Effect<'ServerMsg> = 
        match message with
        | :? ServerNodeMsg as serverNodeMsg ->
            match serverNodeMsg with
            | ServerNodeMsg.AddClient (clientIdentity) ->
                match Map.tryFind clientIdentity.Address ctx.Endpoints with
                | Some _ -> current
                | None ->
                    ctx.SetEndpoints(ctx.Endpoints.Add(clientIdentity.Address, RemoteActor<_>.Create(system, clientIdentity)))
                    current
            | ServerNodeMsg.RemoveClient (address) ->
                match Map.tryFind address ctx.Endpoints with 
                | Some clientIdentity -> 
                    ctx.SetEndpoints(ctx.Endpoints.Remove(address))
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
                match Map.tryFind clientIdentity.Address context.Endpoints with
                | Some _ -> current
                | None ->
                    let addClientMsg = ServerNodeMsg.AddClient clientIdentity
                    clusterSystem.EventStream.Publish(addClientMsg)
                    log.Info (sprintf "[SERVER] Add remote client: %O" clientIdentity.Address)
                    current

            | EndpointMsg.RemoveClient address ->
                match Map.tryFind address context.Endpoints with 
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

            match Map.tryFind sender.Path.Address context.Endpoints, sender.Path.Name = clientRoleName with 
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
    let inline nodeProps (receive: ServerNodeActor<'CallbackMsg, 'ServerMsg>->Effect<'ServerMsg>) : Props<'ServerMsg> = 
        Props<'ServerMsg>.Create<ServerNodeFunActor<'CallbackMsg, 'ServerMsg>, ServerNodeActor<'CallbackMsg, 'ServerMsg>, 'ServerMsg>(receive)

    let inline internal endpointProps clientRoleName (receive: ServerNodeActor<'CallbackMsg, 'ServerMsg>->Effect<'ServerMsg>) : Props<'ServerMsg> = 
        Props<'ServerMsg>.ArgsCreate<ServerEndpointFunActor<'CallbackMsg, 'ServerMsg>, ServerNodeActor<'CallbackMsg, 'ServerMsg>, 'ServerMsg>([| receive; clientRoleName |])


type Server<'CallbackMsg, 'ServerMsg>
    ( systemName, 
      name, 
      clientRoleName: string, 
      remotePort, seedPort, 
      setParams, 
      receive: ServerNodeActor<'CallbackMsg, 'ServerMsg> -> Effect<'ServerMsg> ) =

    let config = Config.createClusterConfig [name] systemName remotePort seedPort setParams
    
    let clusterSystem = System.create systemName config

    let log = clusterSystem.Log

    let actor =
        spawn clusterSystem name (Server.endpointProps clientRoleName (receive))

    member x.Config = config

    member x.ClusterSystem = clusterSystem

    member x.Log = log
