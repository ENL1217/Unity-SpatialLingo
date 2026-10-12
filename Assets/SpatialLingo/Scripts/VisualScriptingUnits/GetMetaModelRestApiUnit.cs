// Copyright (c) Meta Platforms, Inc. and affiliates.
using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;
using Unity.VisualScripting;

namespace SpatialLingo.VisualScriptingUnits
{
    [MetaCodeSample("SpatialLingo")]
    public class GetMetaModelRestApiUnit : Unit
    {

        [DoNotSerialize] private ValueOutput m_metaModelRestApi;
        private MetaModelRestApi m_apiInst;

        protected override void Definition()
        {
            m_metaModelRestApi = ValueOutput(nameof(m_metaModelRestApi), GetMetaModelRestApi);
        }

        private MetaModelRestApi GetMetaModelRestApi(Flow flow)
        {
            return m_apiInst ??= new MetaModelRestApi();
        }
    }
}
