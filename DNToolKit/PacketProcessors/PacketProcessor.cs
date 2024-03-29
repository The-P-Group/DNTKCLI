﻿using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Common;
using Common.Protobuf;
using DNToolKit.Packet;
using DNToolKit.Sniffer;
using Serilog;

namespace DNToolKit.PacketProcessors;

public class PacketProcessor
{
    private readonly ConcurrentQueue<EncryptedPacket> _queue = new();

    private static readonly RSA ClientPrivate = RSA.Create();
    private MTKey? _key;
    private ValueFlag<MTKey> _sessionKey = new();
  

    private bool _useSessionKey;
    private ValueFlag<ulong> _tokenReqSendTime = new();
    private ValueFlag<ulong> _tokenRspServerKey = new();
    

    private byte _timesBFed;

    public int len()
    {
        return _queue.Count;
    }
    public PacketProcessor()
    {
        //todo: take from a file
        ClientPrivate.FromXmlString(Program.Config.ClientPrivateRSA);
        
        _timesBFed = 0;
    }

    public void Start()
    {
        var stop = new Stopwatch();
        stop.Start();
        var count = _queue.Count;

        Work();
        stop.Stop();

        Console.WriteLine("Done! Processed {0} packets in {1} seconds", count, stop.ElapsedMilliseconds/1000d);

    }

    public void Reset()
    {
        _queue.Clear();
        _tokenRspServerKey = new ValueFlag<ulong>();
        _tokenReqSendTime = new ValueFlag<ulong>();
        _useSessionKey = false;
        _sessionKey = new ValueFlag<MTKey>();
        _key = null;
        _timesBFed = 0;
    }

    public void AddPacket(byte[] data, UdpHandler.Sender sender)
    {
        _queue.Enqueue(new EncryptedPacket(data, sender));
    }

    private void Work()
    {
        Log.Information("Processing Packets!");
        //we only start this thread after
        while (_queue.TryDequeue(out var encryptedPacket))
            {

                // File.AppendAllText("./ihatelifept2.txt", $"{encryptedPacket}\n");

                var item = encryptedPacket.Data;
                {
                    if(!_useSessionKey){
                        _key ??= KeyRecovery.FindKey(item);
                        _key?.Crypt(item);
                    }
                    else
                    {
                        if(!_sessionKey.HasBeenSet())
                        {
                            Log.Debug("Bruteforcing Key...");
                            //Program.TestBF((long)tokenReqSendTime, tokenRspServerKey, item);
                            _timesBFed++;
                            Task.WaitAll(new Task[]
                            {
                                _tokenReqSendTime.TaskFlag(),
                                _tokenRspServerKey.TaskFlag()
                            });
                            _sessionKey.SetValue(KeyBruteForcer.BruteForce(item, (long)_tokenReqSendTime.Value, _tokenRspServerKey.Value));
                        }
                        if(!_sessionKey.HasBeenSet()) Log.Warning("something went wrong!");
                        _sessionKey.Value?.Crypt(item);
                        
                        if (_timesBFed > 10)
                        {
                            Log.Error("Brute forcing has failed many times, so make sure you login on a freshly launched client. Or something else could have happened idk");
                            Environment.Exit(-3);
                        }
                    }
                    if (item.GetUInt16(0, true) == 0x4567)
                    {
                        ParsePacketFromData(encryptedPacket);
                    }
                    else if(!_sessionKey.HasBeenSet())
                    {
                        //we may need to bruteforce
                        Log.Warning("Encrypted Packet got through lol");
                        // _sessionKey.TaskFlag().Wait();
                        //should be fine because we store the time 
                        //Queue.Enqueue(encryptedPacket);
                    }
                    else
                    {
                      Log.Warning("There was a false positive with the bruteforcer somehow");
                      Log.Information("@{slay}" ,encryptedPacket.Data);
                    }
                }
            }
        // SpinWait.SpinUntil(()=>Queue.IsEmpty);
    }
    
    
    private void ParsePacketFromData(EncryptedPacket encryptedPacket)
    {

        //todo: i think i need to handle exceptions but i *did* just check packet magic earlier so idk
        try
        {
            //this is SO UGLY
            
            var packet = new Packet.Packet(encryptedPacket.Data)
            {
                Sender = encryptedPacket.Sender
            };
            
            var type = packet.PacketType;
            
            if (type == Opcode.GetPlayerTokenRsp)
            {
                //ideally we do it based on tokenreq but unless your ping is like 3000 we should be fine
                _tokenReqSendTime.SetValue(packet.Metadata.SentMs);
                
                var tokenRsp = packet.PacketData as GetPlayerTokenRsp;
                if (tokenRsp?.EncryptedSeed is not null)
                {
                    var key = ClientPrivate.Decrypt(Convert.FromBase64String(tokenRsp.EncryptedSeed), RSAEncryptionPadding.Pkcs1);
                    _tokenRspServerKey.SetValue(key.GetUInt64(0,true));
                    _useSessionKey = true;
                }
                else
                {
                    Log.Warning("failed to get serverSeed");
                }
            };
        
            if (type == Opcode.UnionCmdNotify)
            {
                UnionCmdProcessor.ProcessUnion(packet);
                return;
            }
            
            if (type == Opcode.CombatInvocationsNotify)
            {
                var list = CombatInvokeProcessor.ProcessCombatInvoke(packet.ProtobufBytes);
                
                
                var fake = new FakePacket<CombatInvokeProcessor.CumbatInvukeNotif>()
                {
                    Metadata = packet.Metadata,
                    PacketType = Opcode.CombatInvocationsNotify,
                    Sender = packet.Sender,
                    DummyPacketData = list.Body as CombatInvokeProcessor.CumbatInvukeNotif
                };
                Program.OutputManager.AddGamePacket(fake);
                return;
            }
            
            if (type == Opcode.AbilityInvocationsNotify)
            {
                var list = AbilityInvokeProcessor.ProcessAbilityInvoke(packet.ProtobufBytes);
                var fake = new FakePacket<AbilityInvokeProcessor.ObilityInvokeNotify>()
                {
                    Metadata = packet.Metadata,
                    PacketType = Opcode.AbilityInvocationsNotify,
                    Sender = packet.Sender,
                    DummyPacketData = list.Body as AbilityInvokeProcessor.ObilityInvokeNotify
                };
                Program.OutputManager.AddGamePacket(fake);
                return;
            }

            if (type == Opcode.ClientAbilityInitFinishNotify)
            {
                var cap = ClientAbilityProcessor.HandleClientAbilityInitFinish(packet.ProtobufBytes);
                var fake = new FakePacket<ClientAbilityProcessor.ClintAbilityInFin>()
                {
                    Metadata = packet.Metadata,
                    PacketType = Opcode.ClientAbilityInitFinishNotify,
                    Sender = packet.Sender,
                    DummyPacketData = cap
                };
                if (fake is null)
                {
                    throw new NullReferenceException("bitch.");
                }
                Program.OutputManager.AddGamePacket(fake);
                return;
            }
            
            if (type == Opcode.ClientAbilityChangeNotify)
            {
                var cap = ClientAbilityProcessor.HandleClientAbilityChange(packet.ProtobufBytes);
                var fake = new FakePacket<ClientAbilityProcessor.ClintAbilityChaeg>()
                {
                    Metadata = packet.Metadata,
                    PacketType = Opcode.ClientAbilityChangeNotify,
                    Sender = packet.Sender,
                    DummyPacketData = cap
                };
                Program.OutputManager.AddGamePacket(fake);
                return;
            }
            
            
            
            Program.OutputManager.AddGamePacket(packet);
        }
        catch(Exception e)
        {
            Log.Error(e.ToString());
        }


    }
    
    private class FakePacket<T>: Packet.Packet
    {
        public T DummyPacketData;
        public override Dictionary<string, object> GetObj()
        {

            Dictionary<string, object> jsonobj = base.GetObj();
            jsonobj.Add("data", DummyPacketData);
            
            

            return jsonobj;
        }
    }
}