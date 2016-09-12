﻿using Lucene.Net.Store;
using Lucene.Net.Support;
using Lucene.Net.Util;
using Lucene.Net.Util.Fst;
using System;
using System.Collections.Generic;
using System.IO;

namespace Lucene.Net.Search.Suggest.Fst
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    /// <summary>
    /// An adapter from <seealso cref="Lookup"/> API to <seealso cref="FSTCompletion"/>.
    /// 
    /// <para>This adapter differs from <seealso cref="FSTCompletion"/> in that it attempts
    /// to discretize any "weights" as passed from in <seealso cref="InputIterator#weight()"/>
    /// to match the number of buckets. For the rationale for bucketing, see
    /// <seealso cref="FSTCompletion"/>.
    /// 
    /// </para>
    /// <para><b>Note:</b>Discretization requires an additional sorting pass.
    /// 
    /// </para>
    /// <para>The range of weights for bucketing/ discretization is determined 
    /// by sorting the input by weight and then dividing into
    /// equal ranges. Then, scores within each range are assigned to that bucket. 
    /// 
    /// </para>
    /// <para>Note that this means that even large differences in weights may be lost 
    /// during automaton construction, but the overall distinction between "classes"
    /// of weights will be preserved regardless of the distribution of weights. 
    /// 
    /// </para>
    /// <para>For fine-grained control over which weights are assigned to which buckets,
    /// use <seealso cref="FSTCompletion"/> directly or <seealso cref="TSTLookup"/>, for example.
    /// 
    /// </para>
    /// </summary>
    /// <seealso cref= FSTCompletion
    /// @lucene.experimental </seealso>
    public class FSTCompletionLookup : Lookup
    {
        /// <summary>
        /// An invalid bucket count if we're creating an object
        /// of this class from an existing FST.
        /// </summary>
        /// <seealso cref= #FSTCompletionLookup(FSTCompletion, boolean) </seealso>
        private static int INVALID_BUCKETS_COUNT = -1;

        /// <summary>
        /// Shared tail length for conflating in the created automaton. Setting this
        /// to larger values (<seealso cref="Integer#MAX_VALUE"/>) will create smaller (or minimal) 
        /// automata at the cost of RAM for keeping nodes hash in the <seealso cref="FST"/>. 
        ///  
        /// <para>Empirical pick.
        /// </para>
        /// </summary>
        private const int sharedTailLength = 5;

        private int buckets;
        private bool exactMatchFirst;

        /// <summary>
        /// Automaton used for completions with higher weights reordering.
        /// </summary>
        private FSTCompletion higherWeightsCompletion;

        /// <summary>
        /// Automaton used for normal completions.
        /// </summary>
        private FSTCompletion normalCompletion;

        /// <summary>
        /// Number of entries the lookup was built with </summary>
        private long count = 0;

        /// <summary>
        /// This constructor prepares for creating a suggested FST using the
        /// <seealso cref="#build(InputIterator)"/> method. The number of weight
        /// discretization buckets is set to <seealso cref="FSTCompletion#DEFAULT_BUCKETS"/> and
        /// exact matches are promoted to the top of the suggestions list.
        /// </summary>
        public FSTCompletionLookup()
            : this(FSTCompletion.DEFAULT_BUCKETS, true)
        {
        }

        /// <summary>
        /// This constructor prepares for creating a suggested FST using the
        /// <seealso cref="#build(InputIterator)"/> method.
        /// </summary>
        /// <param name="buckets">
        ///          The number of weight discretization buckets (see
        ///          <seealso cref="FSTCompletion"/> for details).
        /// </param>
        /// <param name="exactMatchFirst">
        ///          If <code>true</code> exact matches are promoted to the top of the
        ///          suggestions list. Otherwise they appear in the order of
        ///          discretized weight and alphabetical within the bucket. </param>
        public FSTCompletionLookup(int buckets, bool exactMatchFirst)
        {
            this.buckets = buckets;
            this.exactMatchFirst = exactMatchFirst;
        }

        /// <summary>
        /// This constructor takes a pre-built automaton.
        /// </summary>
        ///  <param name="completion"> 
        ///          An instance of <seealso cref="FSTCompletion"/>. </param>
        ///  <param name="exactMatchFirst">
        ///          If <code>true</code> exact matches are promoted to the top of the
        ///          suggestions list. Otherwise they appear in the order of
        ///          discretized weight and alphabetical within the bucket. </param>
        public FSTCompletionLookup(FSTCompletion completion, bool exactMatchFirst)
            : this(INVALID_BUCKETS_COUNT, exactMatchFirst)
        {
            this.normalCompletion = new FSTCompletion(completion.FST, false, exactMatchFirst);
            this.higherWeightsCompletion = new FSTCompletion(completion.FST, true, exactMatchFirst);
        }

        public override void Build(InputIterator iterator)
        {
            if (iterator.HasPayloads)
            {
                throw new System.ArgumentException("this suggester doesn't support payloads");
            }
            if (iterator.HasContexts)
            {
                throw new System.ArgumentException("this suggester doesn't support contexts");
            }
            FileInfo tempInput = FileSupport.CreateTempFile(typeof(FSTCompletionLookup).Name, ".input", OfflineSorter.DefaultTempDir());
            FileInfo tempSorted = FileSupport.CreateTempFile(typeof(FSTCompletionLookup).Name, ".sorted", OfflineSorter.DefaultTempDir());

            OfflineSorter.ByteSequencesWriter writer = new OfflineSorter.ByteSequencesWriter(tempInput);
            OfflineSorter.ByteSequencesReader reader = null;
            ExternalRefSorter sorter = null;

            // Push floats up front before sequences to sort them. For now, assume they are non-negative.
            // If negative floats are allowed some trickery needs to be done to find their byte order.
            bool success = false;
            count = 0;
            try
            {
                byte[] buffer = new byte[0];
                ByteArrayDataOutput output = new ByteArrayDataOutput(buffer);
                BytesRef spare;
                while ((spare = iterator.Next()) != null)
                {
                    if (spare.Length + 4 >= buffer.Length)
                    {
                        buffer = ArrayUtil.Grow(buffer, spare.Length + 4);
                    }

                    output.Reset(buffer);
                    output.WriteInt(EncodeWeight(iterator.Weight));
                    output.WriteBytes(spare.Bytes, spare.Offset, spare.Length);
                    writer.Write(buffer, 0, output.Position);
                }
                writer.Dispose();

                // We don't know the distribution of scores and we need to bucket them, so we'll sort
                // and divide into equal buckets.
                OfflineSorter.SortInfo info = (new OfflineSorter()).Sort(tempInput, tempSorted);
                tempInput.Delete();
                FSTCompletionBuilder builder = new FSTCompletionBuilder(buckets, sorter = new ExternalRefSorter(new OfflineSorter()), sharedTailLength);

                int inputLines = info.Lines;
                reader = new OfflineSorter.ByteSequencesReader(tempSorted);
                long line = 0;
                int previousBucket = 0;
                int previousScore = 0;
                ByteArrayDataInput input = new ByteArrayDataInput();
                BytesRef tmp1 = new BytesRef();
                BytesRef tmp2 = new BytesRef();
                while (reader.Read(tmp1))
                {
                    input.Reset(tmp1.Bytes);
                    int currentScore = input.ReadInt();

                    int bucket;
                    if (line > 0 && currentScore == previousScore)
                    {
                        bucket = previousBucket;
                    }
                    else
                    {
                        bucket = (int)(line * buckets / inputLines);
                    }
                    previousScore = currentScore;
                    previousBucket = bucket;

                    // Only append the input, discard the weight.
                    tmp2.Bytes = tmp1.Bytes;
                    tmp2.Offset = input.Position;
                    tmp2.Length = tmp1.Length - input.Position;
                    builder.Add(tmp2, bucket);

                    line++;
                    count++;
                }

                // The two FSTCompletions share the same automaton.
                this.higherWeightsCompletion = builder.Build();
                this.normalCompletion = new FSTCompletion(higherWeightsCompletion.FST, false, exactMatchFirst);

                success = true;
            }
            finally
            {
                if (success)
                {
                    IOUtils.Close(reader, writer, sorter);
                }
                else
                {
                    IOUtils.CloseWhileHandlingException(reader, writer, sorter);
                }

                tempInput.Delete();
                tempSorted.Delete();
            }
        }

        /// <summary>
        /// weight -> cost </summary>
        private static int EncodeWeight(long value)
        {
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new NotSupportedException("cannot encode value: " + value);
            }
            return (int)value;
        }

        public override IList<LookupResult> DoLookup(string key, IEnumerable<BytesRef> contexts, bool higherWeightsFirst, int num)
        {
            if (contexts != null)
            {
                throw new ArgumentException("this suggester doesn't support contexts");
            }
            IList<FSTCompletion.Completion> completions;
            if (higherWeightsFirst)
            {
                completions = higherWeightsCompletion.DoLookup(key, num);
            }
            else
            {
                completions = normalCompletion.DoLookup(key, num);
            }

            List<LookupResult> results = new List<LookupResult>(completions.Count);
            CharsRef spare = new CharsRef();
            foreach (FSTCompletion.Completion c in completions)
            {
                spare.Grow(c.utf8.Length);
                UnicodeUtil.UTF8toUTF16(c.utf8, spare);
                results.Add(new LookupResult(spare.ToString(), c.bucket));
            }
            return results;
        }

        /// <summary>
        /// Returns the bucket (weight) as a Long for the provided key if it exists,
        /// otherwise null if it does not.
        /// </summary>
        public virtual object Get(string key)
        {
            int bucket = normalCompletion.GetBucket(key);
            return bucket == -1 ? (long?)null : Convert.ToInt64(bucket);
        }

        public override bool Store(DataOutput output)
        {
            lock (this)
            {
                output.WriteVLong(count);
                if (this.normalCompletion == null || normalCompletion.FST == null)
                {
                    return false;
                }
                normalCompletion.FST.Save(output);
                return true;
            }
        }

        public override bool Load(DataInput input)
        {
            lock (this)
            {
                count = input.ReadVLong();
                this.higherWeightsCompletion = new FSTCompletion(new FST<object>(input, NoOutputs.Singleton));
                this.normalCompletion = new FSTCompletion(higherWeightsCompletion.FST, false, exactMatchFirst);
                return true;
            }
        }

        public override long SizeInBytes()
        {
            long mem = RamUsageEstimator.ShallowSizeOf(this) + RamUsageEstimator.ShallowSizeOf(normalCompletion) + RamUsageEstimator.ShallowSizeOf(higherWeightsCompletion);
            if (normalCompletion != null)
            {
                mem += normalCompletion.FST.SizeInBytes();
            }
            if (higherWeightsCompletion != null && (normalCompletion == null || normalCompletion.FST != higherWeightsCompletion.FST))
            {
                // the fst should be shared between the 2 completion instances, don't count it twice
                mem += higherWeightsCompletion.FST.SizeInBytes();
            }
            return mem;
        }

        public override long Count
        {
            get
            {
                return count;
            }
        }
    }
}