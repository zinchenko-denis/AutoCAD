"""Независимые координаты по уточнению Германа 15.10, без CAD-эмуляции."""
import copy
import unittest

from frame_engine import op_frame


def rect(x0,y0,x1,y1):
    return [[x0,y0],[x1,y0],[x1,y1],[x0,y1]]


def request(sub='vertical', side='left', dx=0, dy=0):
    chosen = [0] if side == 'left' else [4000] if side == 'right' else [0,4000] if side == 'both' else []
    return dict(op='frame',sub_type=sub,
        system=dict(name={'vertical':'Вектор-1','ortho':'Ортогональная','interfloor':'Межэтажная'}[sub],
                    mid_rail_over=None,bracket_step=800,bracket_step_corner=500),
        exact_step=True,cladding_offset=230,
        external_corners=[dict(x=dx+x,y=dy+1000) for x in chosen],
        corners_x=[dx+x for x in chosen],
        joints_x=[dx+x for x in (100,300,600,1200,1800,2400,3000,3600,3900)],
        floors_y=[dy+1500] if sub=='interfloor' else [],
        contours=[dict(id='A',pts=rect(dx,dy,dx+4000,dy+3000))])


class CornerRegression(unittest.TestCase):
    def run_ok(self, req):
        result=op_frame(req)
        self.assertTrue(result['ok'],result.get('error'))
        return result

    def test_selected_vertical_two_rows_and_no_intervening_legacy_axis(self):
        for side, expected in [('left',[0,330]),('right',[3670,4000]),('both',[0,330,3670,4000])]:
            with self.subTest(side=side):
                result=self.run_ok(request(side=side))
                axes=sorted({r['x'] for r in result['rails']})
                self.assertTrue(set(expected).issubset(axes))
                if side!='right': self.assertEqual(axes[:2],[0,330])
                if side!='left': self.assertEqual(axes[-2:],[3670,4000])
                for x in expected:
                    self.assertEqual([b['y'] for b in result['brackets'] if b['x']==x],
                                     [300,800,1300,1800,2300,2700])
                self.assertEqual(result['corner_channels'],[])
                self.assertEqual(result['corner_geometry_status'],'partial')
                self.assertTrue(all(i['code']=='placement_not_specified' for i in result['corner_issues']))

    def test_explicit_channel_placement_is_mirrored_and_same_bracket_levels(self):
        for side in ('left','right','both'):
            for overhang in (0,50,100):
                with self.subTest(side=side,overhang=overhang):
                    req=request(side=side)
                    req.update(corner_channel_outer_overhang=overhang,corner_channel_y_offset=0)
                    result=self.run_ok(req)
                    self.assertEqual(result['corner_issues'],[])
                    self.assertEqual(result['corner_geometry_status'],'drawn')
                    self.assertEqual(len(result['corner_channels']),12 if side=='both' else 6)
                    for channel in result['corner_channels']:
                        expected=(-overhang,430-overhang) if channel['side']=='left' else (3570+overhang,4000+overhang)
                        self.assertEqual((channel['x0'],channel['x1']),expected)
                        self.assertEqual(channel['x1']-channel['x0'],430)
                        self.assertEqual(channel['y1']-channel['y0'],100)
                        self.assertEqual(channel['y'],channel['bracket_y'])
                        self.assertEqual(channel['capacity_status'],'not_verified')
                        self.assertEqual(channel['zone'],'контур A')

    def test_y_anchor_not_assumed(self):
        req=request();req.update(corner_channel_outer_overhang=50,corner_channel_y_offset=25)
        result=self.run_ok(req)
        self.assertEqual([c['y'] for c in result['corner_channels']],[325,825,1325,1825,2325,2725])
        req.pop('corner_channel_y_offset')
        result=self.run_ok(req)
        self.assertEqual(result['corner_channels'],[])
        self.assertEqual(result['corner_issues'][0]['code'],'placement_not_specified')

    def test_horizontal_first_axis_and_exact_manual_steps(self):
        for sub in ('ortho','interfloor'):
            for side in ('left','right','both'):
                with self.subTest(sub=sub,side=side):
                    result=self.run_ok(request(sub,side))
                    xs=sorted({b['x'] for b in result['brackets']})
                    expected=([330,830,1330,1830,2630,3430,3850] if side=='left' else
                              [150,570,1370,2170,2670,3170,3670] if side=='right' else
                              [330,830,1330,1830,2330,2830,3330,3670])
                    self.assertEqual(xs,expected)
                    self.assertEqual(result['corner_channels'],[])
                    self.assertEqual(result['corner_issues'],[])

    def test_no_selected_corners_preserves_legacy_geometry_and_clamps(self):
        for sub in ('vertical','ortho','interfloor'):
            req=request(sub,side='none');req.pop('corners_x');req['cladding_offset']=None
            current=self.run_ok(req)
            old=copy.deepcopy(req);old.pop('external_corners');old.pop('cladding_offset')
            legacy=self.run_ok(old)
            for key in ('rails','brackets','clamps','hrails','fittings'):
                self.assertEqual(current[key],legacy[key])
            self.assertEqual(current['corner_geometry_status'],'not_requested')
        req=request();req.update(parts='clamps', rails_fixed=[dict(x=100,y0=0,y1=3000)],rows_y=[500,1000])
        req['cladding_offset']=None
        result=self.run_ok(req)
        self.assertEqual(result['rails'],[])
        self.assertEqual(result['corner_channels'],[])
        self.assertEqual({c['x'] for c in result['clamps']},{100})

    def test_translation_reversed_polygon_and_duplicate_picks(self):
        for dx,dy in ((-3000.125,-8000.25),(10**8+.125,10**8+.25)):
            req=request(side='both',dx=dx,dy=dy)
            req.update(corner_channel_outer_overhang=50,corner_channel_y_offset=0)
            req['contours'][0]['pts'].reverse()
            req['external_corners']*=2
            result=self.run_ok(req)
            self.assertEqual(len(result['corner_channels']),12)
            self.assertEqual(len({(b['x'],b['y']) for b in result['brackets']}),len(result['brackets']))
            self.assertEqual(sorted({r['x']-dx for r in result['rails']})[:2],[0,330])

    def test_actual_vertical_step_edge_limits_height(self):
        req=request();req['contours'][0]['pts']=[[0,0],[4000,0],[4000,3000],[2000,3000],[2000,1500],[0,1500]]
        req['external_corners']=[dict(x=2000,y=2000)];req['corners_x']=[2000]
        req['joints_x']=[600,1200,1800,2600,3200,3800]
        result=self.run_ok(req)
        pairs=[r for r in result['rails'] if r['x'] in (2000,2330)]
        self.assertEqual({r['x'] for r in pairs},{2000,2330})
        self.assertTrue(all(r['y0']==1500 and r['y1']==3000 for r in pairs))
        self.assertFalse(any(2000< b['x']<2330 and b['y']>1500 for b in result['brackets']))

    def test_right_step_boundary_is_not_lost_by_half_open_scan(self):
        req=request();req['contours'][0]['pts']=[[0,0],[4000,0],[4000,1500],[2000,1500],[2000,3000],[0,3000]]
        req['external_corners']=[dict(x=2000,y=2000)];req['corners_x']=[2000]
        req['joints_x']=[600,1200,1800,2600,3200,3800]
        result=self.run_ok(req)
        pairs=[r for r in result['rails'] if r['x'] in (2000,1670)]
        self.assertEqual({r['x'] for r in pairs},{2000,1670})
        self.assertTrue(all(r['y0']==1500 and r['y1']==3000 for r in pairs))

    def test_narrow_pair_and_unmatched_point_cannot_report_drawn(self):
        for sub in ('vertical','ortho','interfloor'):
            req=request(sub,side='both');req['contours'][0]['pts']=rect(0,0,500,3000)
            req['external_corners']=[dict(x=0,y=1000),dict(x=500,y=1000)]
            req['corners_x']=[0,500];req['joints_x']=[250]
            result=self.run_ok(req)
            self.assertEqual(result['corner_geometry_status'],'partial')
            self.assertTrue(any(i['code']=='insufficient_width' for i in result['corner_issues']))
        req=request();req['external_corners']=[dict(x=10000,y=10000)]
        result=self.run_ok(req)
        self.assertEqual(result['corner_geometry_status'],'partial')
        self.assertEqual(result['corner_issues'][0]['code'],'no_matching_edge')

    def test_channel_fractional_coordinates_stay_self_consistent(self):
        req=request(dx=-350.1234567,dy=1200.9876543)
        req.update(cladding_offset=230.1234567,corner_channel_outer_overhang=33.1234567,
                   corner_channel_y_offset=22.9876543)
        result=self.run_ok(req)
        for c in result['corner_channels']:
            self.assertAlmostEqual(c['x1']-c['x0'],c['len'],places=7)
            self.assertAlmostEqual(c['y0']+50,c['y'],places=7)
            self.assertAlmostEqual(c['y1']-50,c['y'],places=7)

    def test_other_zone_corner_is_not_projected_onto_this_facade(self):
        req=request();req['contours'].append(dict(id='B',pts=rect(10000,0,14000,3000)))
        req['joints_x'] += [10600,11200,11800,12400,13000,13600]
        result=self.run_ok(req)
        self.assertFalse(any(r['zone']=='контур B' and r['x'] in (10000,10330) for r in result['rails']))
        self.assertTrue(all(i['zone']=='контур A' for i in result['corner_issues']))

    def test_channel_does_not_cross_opening_or_shorten(self):
        req=request();req.update(corner_channel_outer_overhang=50,corner_channel_y_offset=0)
        req['contours'].append(dict(id='window',pts=rect(100,900,250,1600)))
        result=self.run_ok(req)
        self.assertTrue(result['corner_channels'])
        for c in result['corner_channels']:
            self.assertEqual(c['len'],430)
            self.assertTrue(c['y1']<=900 or c['y0']>=1600)
        self.assertTrue(any(i['code'] in ('opening_or_wall_boundary','unpaired_bracket_level') for i in result['corner_issues']))

    def test_all_claddings_and_calc_offset_independence(self):
        for cladding in ('porcelain','composite','concrete','clinker'):
            req=request();req.update(cladding=cladding,parts='frame',tile_step_x=600,tile_row_step=300)
            result=self.run_ok(req)
            self.assertEqual(sorted({r['x'] for r in result['rails']})[:2],[0,330])
        req=request();req.update(exact_step=False,
            calc=dict(wind_region='II',terrain='B',height=6,q_clad=25,offset=170,na_max=3000,auto_profile=False))
        result=self.run_ok(req)
        self.assertEqual(result['calc_report']['inputs']['offset'],170)
        self.assertEqual(sorted({r['x'] for r in result['rails']})[:2],[0,330])
        self.assertTrue(result['brackets'])

    def test_vertical_corner_axes_do_not_require_unrelated_cladding_joints(self):
        req=request();req['joints_x']=[]
        req.update(corner_channel_outer_overhang=25,corner_channel_y_offset=0,cladding_offset=345.75)
        result=self.run_ok(req)
        self.assertEqual(sorted({r['x'] for r in result['rails']})[:2],[0,445.75])
        self.assertTrue(result['corner_channels'])
        self.assertEqual({c['len'] for c in result['corner_channels']},{545.75})

    def test_channel_without_any_candidate_bracket_level_is_not_drawn(self):
        import frame_corners
        from frame_plan import _hspans
        poly=rect(0,0,4000,3000)
        edges=frame_corners.selected_edges(poly,[(0,1000)],230)
        channels,issues=frame_corners.channels(edges,[],[],poly,[],_hspans,(50,0))
        self.assertEqual(channels,[])
        self.assertEqual(issues[0]['reason'],'unpaired_bracket_level')

    def test_invalid_parameters_fail_before_geometry(self):
        for value in (-1,float('nan'),float('inf'),True,'bad',None):
            req=request();req['cladding_offset']=value
            result=op_frame(req)
            self.assertFalse(result['ok'])
            self.assertEqual(result['error_code'],'E_FRAME_INPUT')
        req=request();req.update(corner_channel_outer_overhang=101,corner_channel_y_offset=0)
        self.assertFalse(op_frame(req)['ok'])


if __name__=='__main__':unittest.main()
