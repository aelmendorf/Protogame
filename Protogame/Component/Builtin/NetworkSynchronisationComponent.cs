﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Xna.Framework;
using Protoinject;

namespace Protogame
{
    public partial class NetworkSynchronisationComponent : IUpdatableComponent, IServerUpdatableComponent, INetworkedComponent, INetworkIdentifiable, ISynchronisationApi
    {
        private readonly INetworkEngine _networkEngine;
        private readonly IUniqueIdentifierAllocator _uniqueIdentifierAllocator;
        private readonly INetworkMessageSerialization _networkMessageSerialization;

        private int? _uniqueIdentifierForEntity;

        private readonly List<IPEndPoint> _clientsEntityIsKnownOn;

        private readonly Dictionary<string, SynchronisedData> _synchronisedData;
        private readonly List<SynchronisedData> _synchronisedDataToTransmit;
        private ISynchronisedObject _synchronisationContext;

        private int _localTick;

        public NetworkSynchronisationComponent(
            INetworkEngine networkEngine,
            IUniqueIdentifierAllocator uniqueIdentifierAllocator,
            INetworkMessageSerialization networkMessageSerialization)
        {
            _networkEngine = networkEngine;
            _uniqueIdentifierAllocator = uniqueIdentifierAllocator;
            _networkMessageSerialization = networkMessageSerialization;

            _clientsEntityIsKnownOn = new List<IPEndPoint>();
            _synchronisedData = new Dictionary<string, SynchronisedData>();
            _synchronisedDataToTransmit = new List<SynchronisedData>();
        }

        /// <summary>
        /// Whether this entity and it's components only exist on the server.  If this
        /// is set to true, no data is sent to clients about this entity.
        /// </summary>
        public bool ServerOnly { get; set; }

        /// <summary>
        /// Whether this entity only exists on the server and the authoritive client.  If
        /// clients have no authority over this entity, this option has no effect.  If this
        /// option is false, then all clients are made aware of a client authoritive entity
        /// (even clients who do not control it).
        /// </summary>
        public bool OnlySendToAuthoritiveClient { get; set; }

        /// <summary>
        /// The client which owns this entity.  If <see cref="ClientAuthoritiveMode"/> is
        /// anything other than <c>None</c>, then this indicates which clients can modify
        /// information about this entity on the server.  If this is <c>null</c> while
        /// <see cref="ClientAuthoritiveMode"/> is anything other than <c>None</c>, then any
        /// client can modify this entity.
        /// </summary>
        public IPEndPoint ClientOwnership { get; set; }

        /// <summary>
        /// The authority level given to clients over this entity.
        /// </summary>
        public ClientAuthoritiveMode ClientAuthoritiveMode { get; set; }

        public void Update(ComponentizedEntity entity, IGameContext gameContext, IUpdateContext updateContext)
        {
            _localTick++;

            if (!_uniqueIdentifierForEntity.HasValue)
            {
                // TODO: Support predicted entities here.
                // We don't have an identifier provided by the server, so skip all this logic.
                return;
            }

            if (ClientAuthoritiveMode != ClientAuthoritiveMode.None)
            {
                // This client has some level of authority over the entity, so send data that's important.
                switch (ClientAuthoritiveMode)
                {
                    case ClientAuthoritiveMode.TrustClient:
                        PrepareAndTransmitSynchronisation(entity, _localTick, true, ClientAuthoritiveMode);
                        break;
                    case ClientAuthoritiveMode.ReplayInputs:
                        throw new NotSupportedException("Replaying inputs provided by clients is not yet supported.");
                    default:
                        throw new InvalidOperationException("Unknown client authoritivity mode: " + ClientAuthoritiveMode);
                }

            }
        }

        public void Update(ComponentizedEntity entity, IServerContext serverContext, IUpdateContext updateContext)
        {
            if (_uniqueIdentifierForEntity == null)
            {
                _uniqueIdentifierForEntity = _uniqueIdentifierAllocator.Allocate();
                _networkEngine.RegisterObjectAsNetworkId(_uniqueIdentifierForEntity.Value, entity);
            }

            if (ServerOnly)
            {
                return;
            }
            
            // Sync the entity to the client if it hasn't been already.
            foreach (var dispatcher in _networkEngine.CurrentDispatchers)
            {
                // TODO: Tracking clients by endpoint almost certainly needs to change...
                foreach (var endpoint in dispatcher.Endpoints)
                {
                    if (ClientAuthoritiveMode != ClientAuthoritiveMode.None &&
                        ClientOwnership != null && 
                        OnlySendToAuthoritiveClient)
                    {
                        if (!ClientOwnership.Equals(endpoint))
                        {
                            // This client doesn't own the entity, and this entity is only
                            // synchronised with clients that own it.
                            continue;
                        }
                    }

                    if (!_clientsEntityIsKnownOn.Contains(endpoint))
                    {
                        // Send an entity creation message to the client.
                        var createMessage = new EntityCreateMessage
                        {
                            EntityID = _uniqueIdentifierForEntity.Value,
                            EntityType = entity.GetType().AssemblyQualifiedName,
                            InitialTransform = entity.Transform.SerializeToNetwork(),
                        };
                        dispatcher.Send(
                            endpoint,
                            _networkMessageSerialization.Serialize(createMessage),
                            true);

                        _clientsEntityIsKnownOn.Add(endpoint);
                    }
                }
            }
            
            PrepareAndTransmitSynchronisation(entity, serverContext.Tick, false, ClientAuthoritiveMode);
        }

        public bool ReceiveMessage(ComponentizedEntity entity, IGameContext gameContext, IUpdateContext updateContext, MxDispatcher dispatcher, MxClient server,
            byte[] payload, uint protocolId)
        {
            if (_uniqueIdentifierForEntity == null)
            {
                return false;
            }

            var propertyMessage = _networkMessageSerialization.Deserialize(payload) as EntityPropertiesMessage;

            if (propertyMessage == null || propertyMessage.EntityID != _uniqueIdentifierForEntity.Value)
            {
                return false;
            }

            // If the entity is a synchronised entity, collect properties of the synchronised object
            // directly.
            var synchronisedEntity = entity as ISynchronisedObject;
            if (synchronisedEntity != null)
            {
                _synchronisationContext = synchronisedEntity;
                _synchronisationContext.DeclareSynchronisedProperties(this);
            }

            // Iterate through all the components on the entity and get their synchronisation data as well.
            foreach (var synchronisedComponent in entity.Components.OfType<ISynchronisedObject>())
            {
                _synchronisationContext = synchronisedComponent;
                _synchronisationContext.DeclareSynchronisedProperties(this);
            }

            AssignMessageToSyncData(propertyMessage, _synchronisedData);
            return true;
        }

        public bool ReceiveMessage(ComponentizedEntity entity, IServerContext serverContext, IUpdateContext updateContext, MxDispatcher dispatcher, MxClient client, byte[] payload, uint protocolId)
        {
            if (_uniqueIdentifierForEntity == null)
            {
                return false;
            }

            // See what kind of messages we accept, based on the client authority.
            switch (ClientAuthoritiveMode)
            {
                case ClientAuthoritiveMode.None:
                    // We don't accept any client data about this entity, so ignore it.
                    return false;
                case ClientAuthoritiveMode.TrustClient:
                    {
                        // Check to see if the message is coming from a client that has authority.
                        if (ClientOwnership != null && !ClientOwnership.Equals(client.Endpoint))
                        {
                            // We don't trust this message.
                            return false;
                        }

                        // We trust the client, so process this information like a client would.
                        var propertyMessage = _networkMessageSerialization.Deserialize(payload) as EntityPropertiesMessage;

                        if (propertyMessage == null || propertyMessage.EntityID != _uniqueIdentifierForEntity.Value)
                        {
                            return false;
                        }

                        // If the entity is a synchronised entity, collect properties of the synchronised object
                        // directly.
                        var synchronisedEntity = entity as ISynchronisedObject;
                        if (synchronisedEntity != null)
                        {
                            _synchronisationContext = synchronisedEntity;
                            _synchronisationContext.DeclareSynchronisedProperties(this);
                        }

                        // Iterate through all the components on the entity and get their synchronisation data as well.
                        foreach (var synchronisedComponent in entity.Components.OfType<ISynchronisedObject>())
                        {
                            _synchronisationContext = synchronisedComponent;
                            _synchronisationContext.DeclareSynchronisedProperties(this);
                        }

                        AssignMessageToSyncData(propertyMessage, _synchronisedData);
                        return true;
                    }
                case ClientAuthoritiveMode.ReplayInputs:
                    // We don't implement this yet, but we don't want to allow client packets to cause
                    // a server error, so silently consume it.
                    return false;
            }

            return false;
        }

        public void ReceiveNetworkIDFromServer(IGameContext gameContext, IUpdateContext updateContext, int identifier)
        {
            _uniqueIdentifierForEntity = identifier;
        }

        public void ReceivePredictedNetworkIDFromClient(IServerContext serverContext, IUpdateContext updateContext, MxClient client,
            int predictedIdentifier)
        {
            
        }

        public void Synchronise<T>(string name, int frameInterval, T currentValue, Action<T> setValue)
        {
            // TODO: Make this value more unique, and synchronised across the network (so we can have multiple components of the same type).
            var context = "unknown";
            if (_synchronisationContext is ComponentizedEntity)
            {
                context = "entity";
            }
            else
            {
                context = _synchronisationContext.GetType().Name;
            }
            var contextFullName = context + "." + name;

            // Find or add synchronisation data.
            SynchronisedData entry;
            if (_synchronisedData.ContainsKey(contextFullName))
            {
                entry = _synchronisedData[contextFullName];
            }
            else
            {
                _synchronisedData[contextFullName] = new SynchronisedData
                {
                    Name = contextFullName,
                    HasPerformedInitialSync = false,
                };

                entry = _synchronisedData[contextFullName];
            }

            _synchronisedData[contextFullName].IsActiveInSynchronisation = true;
            _synchronisedData[contextFullName].FrameInterval = frameInterval;
            _synchronisedData[contextFullName].LastValue = _synchronisedData[contextFullName].CurrentValue;
            _synchronisedData[contextFullName].CurrentValue = currentValue;
            _synchronisedData[contextFullName].SetValueDelegate = x => { setValue((T)x); }; // TODO: This causes a memory allocation.
        }

        private class SynchronisedData
        {
            public string Name;

            public int FrameInterval;

            public object LastValue;

            public object CurrentValue;

            public int LastFrameSynced;

            public bool HasPerformedInitialSync;

            public Action<object> SetValueDelegate;

            public bool IsActiveInSynchronisation;

            public bool HasReceivedInitialSync;
        }

        #region Synchronisation Preperation
        
        private void PrepareAndTransmitSynchronisation(ComponentizedEntity entity, int tick, bool isFromClient, ClientAuthoritiveMode clientAuthoritiveMode)
        {
            if (!_uniqueIdentifierForEntity.HasValue)
            {
                throw new InvalidOperationException("PrepareAndTransmit should not be called without an entity ID!");
            }

            // If the entity is a synchronised entity, collect properties of the synchronised object
            // directly.
            var synchronisedEntity = entity as ISynchronisedObject;
            if (synchronisedEntity != null)
            {
                _synchronisationContext = synchronisedEntity;
                _synchronisationContext.DeclareSynchronisedProperties(this);
            }

            // Iterate through all the components on the entity and get their synchronisation data as well.
            foreach (var synchronisedComponent in entity.Components.OfType<ISynchronisedObject>())
            {
                _synchronisationContext = synchronisedComponent;
                _synchronisationContext.DeclareSynchronisedProperties(this);
            }

            if (_synchronisedData.Count > 0)
            {
                // Now calculate the delta to transmit over the network.
                var currentTick = tick; // TODO: Use TimeTick
                _synchronisedDataToTransmit.Clear();
                foreach (var data in _synchronisedData.Values)
                {
                    var needsSync = false;

                    // If we're on the client and we haven't had an initial piece of data from the server,
                    // we never synchronise because we don't know what the initial value is.
                    if (isFromClient && !data.HasReceivedInitialSync)
                    {
                        continue;
                    }

                    // If we haven't performed the initial synchronisation, we always transmit the data.
                    if (!data.HasPerformedInitialSync)
                    {
                        needsSync = true;
                    }

                    // If we are on the client (i.e. the client assumes it's authoritive), or if the 
                    // server knows that the client does not have authority, then allow this next section.
                    // Or to put it another way, if we're not on the client and we know the client has
                    // authority, only transmit data for the first time because the client will make 
                    // decisions from that point onwards.
                    if (isFromClient || clientAuthoritiveMode != ClientAuthoritiveMode.TrustClient)
                    {
                        if (data.LastValue != data.CurrentValue)
                        {
                            if (data.LastFrameSynced + data.FrameInterval < currentTick)
                            {
                                needsSync = true;
                            }
                        }
                    }

                    if (needsSync)
                    {
                        _synchronisedDataToTransmit.Add(data);
                    }
                }

                if (_synchronisedDataToTransmit.Count > 0)
                {
                    // Build up the synchronisation message.
                    var message = new EntityPropertiesMessage();
                    message.EntityID = _uniqueIdentifierForEntity.Value;
                    message.FrameTick = currentTick;
                    message.PropertyNames = new string[_synchronisedDataToTransmit.Count];
                    message.PropertyTypes = new int[_synchronisedDataToTransmit.Count];
                    message.IsClientMessage = isFromClient;

                    bool reliable;
                    AssignSyncDataToMessage(_synchronisedDataToTransmit, message, currentTick, out reliable);

                    // Sync properties to each client.
                    foreach (var dispatcher in _networkEngine.CurrentDispatchers)
                    {
                        // TODO: Tracking clients by endpoint almost certainly needs to change...
                        foreach (var endpoint in dispatcher.Endpoints)
                        {
                            if (ClientAuthoritiveMode != ClientAuthoritiveMode.None &&
                                ClientOwnership != null &&
                                OnlySendToAuthoritiveClient)
                            {
                                if (!ClientOwnership.Equals(endpoint))
                                {
                                    // This client doesn't own the entity, and this entity is only
                                    // synchronised with clients that own it.
                                    continue;
                                }
                            }
                            
                            if (isFromClient || _clientsEntityIsKnownOn.Contains(endpoint))
                            {
                                // Send an entity properties message to the client.
                                dispatcher.Send(
                                    endpoint,
                                    _networkMessageSerialization.Serialize(message),
                                    reliable);
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region Conversion Methods

        public float[] ConvertToVector2(object obj)
        {
            var vector = (Vector2) obj;
            return new[] { vector.X, vector.Y };
        }

        public float[] ConvertToVector3(object obj)
        {
            var vector = (Vector3)obj;
            return new[] { vector.X, vector.Y, vector.Z };
        }

        public float[] ConvertToVector4(object obj)
        {
            var vector = (Vector4)obj;
            return new[] { vector.X, vector.Y, vector.Z, vector.W };
        }

        public float[] ConvertToQuaternion(object obj)
        {
            var quat = (Quaternion)obj;
            return new[] { quat.X, quat.Y, quat.Z, quat.W };
        }

        public float[] ConvertToMatrix(object obj)
        {
            var matrix = (Matrix)obj;
            return new[]
            {
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44,
            };
        }

        public NetworkTransform ConvertToTransform(object obj)
        {
            var transform = (ITransform)obj;
            return transform.SerializeToNetwork();
        }

        public Vector2 ConvertFromVector2(float[] obj, int offset)
        {
            return new Vector2(
                obj[offset],
                obj[offset + 1]);
        }

        public Vector3 ConvertFromVector3(float[] obj, int offset)
        {
            return new Vector3(
                obj[offset],
                obj[offset + 1],
                obj[offset + 2]);
        }

        public Vector4 ConvertFromVector4(float[] obj, int offset)
        {
            return new Vector4(
                obj[offset],
                obj[offset + 1],
                obj[offset + 2],
                obj[offset + 3]);
        }

        public Quaternion ConvertFromQuaternion(float[] obj, int offset)
        {
            return new Quaternion(
                obj[offset],
                obj[offset + 1],
                obj[offset + 2],
                obj[offset + 3]);
        }

        public Matrix ConvertFromMatrix(float[] obj, int offset)
        {
            return new Matrix(
                obj[offset],
                obj[offset + 1],
                obj[offset + 2],
                obj[offset + 3],
                obj[offset + 4],
                obj[offset + 5],
                obj[offset + 6],
                obj[offset + 7],
                obj[offset + 8],
                obj[offset + 9],
                obj[offset + 10],
                obj[offset + 11],
                obj[offset + 12],
                obj[offset + 13],
                obj[offset + 14],
                obj[offset + 15]);
        }

        public ITransform ConvertFromTransform(NetworkTransform obj)
        {
            return obj.DeserializeFromNetwork();
        }


        #endregion
    }
}