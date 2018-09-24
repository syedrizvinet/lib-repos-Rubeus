﻿using System;
using Asn1;
using System.Text;
using System.Collections.Generic;

namespace Rubeus
{
    public class Authenticator
    {
        //Authenticator   ::= [APPLICATION 2] SEQUENCE  {
        //        authenticator-vno       [0] INTEGER (5),
        //        crealm                  [1] Realm,
        //        cname                   [2] PrincipalName,
        //        cksum                   [3] Checksum OPTIONAL,
        //        cusec                   [4] Microseconds,
        //        ctime                   [5] KerberosTime,
        //        subkey                  [6] EncryptionKey OPTIONAL,
        //        seq-number              [7] UInt32 OPTIONAL,
        //        authorization-data      [8] AuthorizationData OPTIONAL
        //}

        // NOTE: we're only using:
        //  authenticator-vno   [0]
        //  crealm              [1]
        //  cname               [2]
        //  cusec               [4]
        //  ctime               [5]

        public Authenticator()
        {
            authenticator_vno = 5;

            crealm = "";

            cname = new PrincipalName();

            cusec = 0;

            ctime = DateTime.UtcNow;
        }

        public AsnElt Encode()
        {
            List<AsnElt> allNodes = new List<AsnElt>();


            // authenticator-vno       [0] INTEGER (5)
            AsnElt pvnoAsn = AsnElt.MakeInteger(authenticator_vno);
            AsnElt pvnoSeq = AsnElt.Make(AsnElt.SEQUENCE, new[] { pvnoAsn });
            pvnoSeq = AsnElt.MakeImplicit(AsnElt.CONTEXT, 0, pvnoSeq);
            allNodes.Add(pvnoSeq);


            // crealm                  [1] Realm
            AsnElt realmAsn = AsnElt.MakeString(AsnElt.IA5String, crealm);
            realmAsn = AsnElt.MakeImplicit(AsnElt.UNIVERSAL, AsnElt.GeneralString, realmAsn);
            AsnElt realmSeq = AsnElt.Make(AsnElt.SEQUENCE, new[] { realmAsn });
            realmSeq = AsnElt.MakeImplicit(AsnElt.CONTEXT, 1, realmSeq);
            allNodes.Add(realmSeq);


            // cname                   [2] PrincipalName
            AsnElt snameElt = cname.Encode();
            snameElt = AsnElt.MakeImplicit(AsnElt.CONTEXT, 2, snameElt);
            allNodes.Add(snameElt);


            // TODO: correct format (UInt32)?
            // cusec                   [4] Microseconds
            AsnElt nonceAsn = AsnElt.MakeInteger(cusec);
            AsnElt nonceSeq = AsnElt.Make(AsnElt.SEQUENCE, new[] { nonceAsn });
            nonceSeq = AsnElt.MakeImplicit(AsnElt.CONTEXT, 4, nonceSeq);
            allNodes.Add(nonceSeq);


            // ctime                   [5] KerberosTime
            AsnElt tillAsn = AsnElt.MakeString(AsnElt.GeneralizedTime, ctime.ToString("yyyyMMddHHmmssZ"));
            AsnElt tillSeq = AsnElt.Make(AsnElt.SEQUENCE, new[] { tillAsn });
            tillSeq = AsnElt.MakeImplicit(AsnElt.CONTEXT, 5, tillSeq);
            allNodes.Add(tillSeq);


            // package it all up
            AsnElt seq = AsnElt.Make(AsnElt.SEQUENCE, allNodes.ToArray());


            // tag the final total
            AsnElt final = AsnElt.Make(AsnElt.SEQUENCE, new AsnElt[] { seq });
            final = AsnElt.MakeImplicit(AsnElt.APPLICATION, 2, final);

            return final;
        }


        public long authenticator_vno { get; set; }

        public string crealm { get; set; }

        public PrincipalName cname { get; set; }

        public long cusec { get; set; }

        public DateTime ctime { get; set; }
    }
}