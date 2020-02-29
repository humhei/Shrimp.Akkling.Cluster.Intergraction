namespace Shrimp.Akkling.Cluster.Intergraction
open Akkling
open Akka.Actor


//type DummyResponse = DummyResponse
[<RequireQualifiedAccess>]
type SerializableOption<'T> =
    | Some of 'T
    | None
[<RequireQualifiedAccess>]
module SerializableOption =
    let toOption = function
        | SerializableOption.Some v -> Some v
        | SerializableOption.None -> None


[<RequireQualifiedAccess>]
type ErrorResponse = 
    | ServerText of string
    | ServerException of System.Exception
    | ClientText of string
with 
    override x.ToString() =
        match x with 
        | ErrorResponse.ServerText errorMsg -> errorMsg
        | ErrorResponse.ServerException ex -> ex.ToString()
        | ErrorResponse.ClientText errorMsg -> errorMsg

type ErrorResponseException(errorResponse: ErrorResponse) =
    inherit System.Exception()

    member x.ErrorResponse = errorResponse

    override x.ToString() = errorResponse.ToString()


[<RequireQualifiedAccess>]
type ErrorNotifycation =
    | ServerText of string
    | ServerException of System.Exception
with 
    override x.ToString() =
        match x with 
        | ErrorNotifycation.ServerText errorMsg -> errorMsg
        | ErrorNotifycation.ServerException ex -> ex.ToString()

type RemoteActorIdentity =
    { Address: Address 
      Role: string }


type RemoteActor<'Msg> private (clusterSystem: ActorSystem, address: Address, role: string) =
    
    let remotePath = sprintf "%O/user/%s" address role

    let actor = select clusterSystem remotePath 

    member x.Address = address

    member x.ClusterSystem = clusterSystem

    member x.Role = role

    member x.GetIdentity() = 
        { Role = role 
          Address = address }


    override x.Equals(yobj) =
        match yobj with
        | :? RemoteActor<'Msg> as y -> (x.Address = y.Address)
        | _ -> false

    override x.GetHashCode() = hash x.Address
    interface ICanTell<'Msg> with 
        member x.Ask(msg, ?timeSpan) = (actor :> ICanTell<'Msg>).Ask(msg, timeSpan)
        
        member x.Tell (msg, actorRef) = (actor :> ICanTell<'Msg>).Tell(msg, actorRef)

        member x.Underlying = (actor :> ICanTell<'Msg>).Underlying

    interface System.IComparable with
        member x.CompareTo yobj =
            match yobj with
            | :? RemoteActor<'Msg> as y -> compare x.Address y.Address
            | _ -> invalidArg "yobj" "cannot compare values of different types"
        
    static member Create(clusterSystem: ActorSystem, address: Address, role: string) =
        RemoteActor(clusterSystem, address, role)

    static member Create(clusterSystem: ActorSystem, identity: RemoteActorIdentity) =
        RemoteActor(clusterSystem, identity.Address, identity.Role)


[<RequireQualifiedAccess>]
module private RemoteActor =
    let retype (remoteActor: RemoteActor<_>) =
        RemoteActor<_>.Create(remoteActor.ClusterSystem, remoteActor.Address, remoteActor.Role)

[<RequireQualifiedAccess>]
type RemoteActorReachable =
    | Yes
    | No

[<AutoOpen>]
module private InternalTypes =


    [<RequireQualifiedAccess>]
    type EndpointMsg =
        | AddServer of RemoteActorIdentity
        | RemoveServer of Address
        | AddClient of RemoteActorIdentity
        | RemoveClient of Address


    type ServerMsgToken<'ServerMsg> =
        { ServerMsg: 'ServerMsg
          Guid: System.Guid }