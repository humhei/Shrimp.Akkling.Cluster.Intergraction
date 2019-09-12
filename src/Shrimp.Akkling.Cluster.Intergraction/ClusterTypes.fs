namespace Shrimp.Akkling.Cluster.Intergraction
open Akkling
open Akka.Actor

type ErrorResponse = ErrorResponse of string

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
module RemoteActor =
    let retype (remoteActor: RemoteActor<_>) =
        RemoteActor<_>.Create(remoteActor.ClusterSystem, remoteActor.Address, remoteActor.Role)

[<AutoOpen>]
module private InternalTypes =



    type UpdateClientsEvent<'ClientCallbackMsg> = UpdateClientsEvent of Map<Address, RemoteActor<'ClientCallbackMsg>>

    [<RequireQualifiedAccess>]
    type RemoteActorReachable =
        | Yes
        | No

    [<RequireQualifiedAccess>]
    type EndpointMsg =
        | AddServer of RemoteActorIdentity
        | RemoveServer of Address
        | AddClient of RemoteActorIdentity
        | RemoveClient of Address
